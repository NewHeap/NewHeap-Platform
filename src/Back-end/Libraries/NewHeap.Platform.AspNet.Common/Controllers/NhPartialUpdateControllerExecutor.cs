using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.Controllers;

internal static class NhPartialUpdateControllerExecutor
{
    public static async Task<IActionResult> ExecuteAsync<TUpdateMutateModel, TEntity>(
        ControllerBase controller,
        IStringLocalizer localizer,
        JObject? partialUpdate,
        Func<string, bool> canPartiallyUpdateProperty,
        Func<Func<NhSetPropertyCalls<TUpdateMutateModel>, NhSetPropertyCalls<TUpdateMutateModel>>, Task<TaskResult<TEntity?>>> updateAsync)
        where TUpdateMutateModel : class
        where TEntity : class
    {
        if (!TryCreateMapping<TUpdateMutateModel>(
                controller,
                localizer,
                partialUpdate,
                canPartiallyUpdateProperty,
                out var mapping))
        {
            return controller.BadRequest(controller.ModelState);
        }

        if (!mapping.HasChanges)
        {
            return controller.NoContent();
        }

        var updateResult = await updateAsync(mapping.Apply);
        if (!updateResult.Success)
        {
            updateResult.ApplyToModelState(controller.ModelState);
            return controller.BadRequest(controller.ModelState);
        }

        return controller.NoContent();
    }

    internal static bool TryCreateMapping<TUpdateMutateModel>(
        ControllerBase controller,
        IStringLocalizer localizer,
        JObject? partialUpdate,
        Func<string, bool> canPartiallyUpdateProperty,
        out NhPartialUpdateMapping<TUpdateMutateModel> mapping)
        where TUpdateMutateModel : class
    {
        mapping = null!;

        if (!controller.ModelState.IsValid)
        {
            return false;
        }

        if (partialUpdate is null)
        {
            controller.ModelState.AddModelError(
                string.Empty,
                Localize(localizer, "Invalid request"));
            return false;
        }

        var serializer = CreateSerializer(controller);
        mapping = NhPartialUpdateMapper.Map<TUpdateMutateModel>(
            partialUpdate,
            serializer,
            canPartiallyUpdateProperty);

        foreach (var error in mapping.Errors)
        {
            controller.ModelState.AddModelError(
                error.PropertyName,
                Localize(localizer, error.Message));
        }

        return controller.ModelState.IsValid;
    }

    private static JsonSerializer CreateSerializer(ControllerBase controller)
    {
        var options = controller.HttpContext.RequestServices
            .GetService<IOptions<MvcNewtonsoftJsonOptions>>();

        return options is null
            ? JsonSerializer.CreateDefault()
            : JsonSerializer.Create(options.Value.SerializerSettings);
    }

    private static string Localize(IStringLocalizer localizer, string key)
    {
        var value = localizer[key]?.Value;
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }
}

internal static class NhPartialUpdateMapper
{
    public static NhPartialUpdateMapping<TUpdateMutateModel> Map<TUpdateMutateModel>(
        JObject partialUpdate,
        JsonSerializer serializer,
        Func<string, bool> canPartiallyUpdateProperty)
        where TUpdateMutateModel : class
    {
        var errors = new List<NhPartialUpdateMappingError>();
        var assignments = new List<NhPartialUpdateAssignment<TUpdateMutateModel>>();
        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (serializer.ContractResolver.ResolveContract(typeof(TUpdateMutateModel)) is not JsonObjectContract contract)
        {
            errors.Add(new NhPartialUpdateMappingError(string.Empty, "Invalid request"));
            return new NhPartialUpdateMapping<TUpdateMutateModel>(assignments, errors);
        }

        foreach (var inputProperty in partialUpdate.Properties())
        {
            var jsonProperty = contract.Properties.GetClosestMatchProperty(inputProperty.Name);
            var clrProperty = GetWritableProperty<TUpdateMutateModel>(jsonProperty);

            if (jsonProperty is null || jsonProperty.Ignored || !jsonProperty.Writable || clrProperty is null)
            {
                errors.Add(new NhPartialUpdateMappingError(
                    inputProperty.Name,
                    "Unknown partial-update property"));
                continue;
            }

            if (!seenProperties.Add(clrProperty.Name))
            {
                errors.Add(new NhPartialUpdateMappingError(
                    inputProperty.Name,
                    "Duplicate partial-update property"));
                continue;
            }

            if (!canPartiallyUpdateProperty(clrProperty.Name))
            {
                errors.Add(new NhPartialUpdateMappingError(
                    inputProperty.Name,
                    "Partial-update property is not allowed"));
                continue;
            }

            if (!TryDeserialize(
                    inputProperty.Value,
                    clrProperty.PropertyType,
                    jsonProperty.Converter,
                    serializer,
                    out var value))
            {
                errors.Add(new NhPartialUpdateMappingError(
                    inputProperty.Name,
                    "Invalid partial-update value"));
                continue;
            }

            assignments.Add(new NhPartialUpdateAssignment<TUpdateMutateModel>(
                SetterCache<TUpdateMutateModel>.GetOrAdd(clrProperty),
                value));
        }

        return new NhPartialUpdateMapping<TUpdateMutateModel>(assignments, errors);
    }

    private static PropertyInfo? GetWritableProperty<TUpdateMutateModel>(JsonProperty? jsonProperty)
        where TUpdateMutateModel : class
    {
        if (string.IsNullOrWhiteSpace(jsonProperty?.UnderlyingName))
        {
            return null;
        }

        var property = typeof(TUpdateMutateModel).GetProperty(
            jsonProperty.UnderlyingName,
            BindingFlags.Instance | BindingFlags.Public);

        return property?.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0
            ? property
            : null;
    }

    private static bool TryDeserialize(
        JToken token,
        Type propertyType,
        JsonConverter? propertyConverter,
        JsonSerializer serializer,
        out object? value)
    {
        value = null;

        if (token.Type == JTokenType.Null)
        {
            return !propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) is not null;
        }

        try
        {
            using var reader = token.CreateReader();
            if (propertyConverter?.CanRead == true)
            {
                reader.Read();
                value = propertyConverter.ReadJson(
                    reader,
                    propertyType,
                    existingValue: null,
                    serializer);
            }
            else
            {
                value = serializer.Deserialize(reader, propertyType);
            }

            return value is not null ||
                   !propertyType.IsValueType ||
                   Nullable.GetUnderlyingType(propertyType) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static class SetterCache<TUpdateMutateModel>
        where TUpdateMutateModel : class
    {
        private static readonly ConcurrentDictionary<
            PropertyInfo,
            Func<NhSetPropertyCalls<TUpdateMutateModel>, object?, NhSetPropertyCalls<TUpdateMutateModel>>> Setters = new();

        public static Func<NhSetPropertyCalls<TUpdateMutateModel>, object?, NhSetPropertyCalls<TUpdateMutateModel>> GetOrAdd(
            PropertyInfo property)
        {
            return Setters.GetOrAdd(property, CreateSetter);
        }

        private static Func<NhSetPropertyCalls<TUpdateMutateModel>, object?, NhSetPropertyCalls<TUpdateMutateModel>> CreateSetter(
            PropertyInfo property)
        {
            var callsParameter = Expression.Parameter(
                typeof(NhSetPropertyCalls<TUpdateMutateModel>),
                "calls");
            var valueParameter = Expression.Parameter(typeof(object), "value");
            var modelParameter = Expression.Parameter(typeof(TUpdateMutateModel), "model");

            var selectorType = typeof(Func<,>).MakeGenericType(
                typeof(TUpdateMutateModel),
                property.PropertyType);
            var selector = Expression.Lambda(
                selectorType,
                Expression.Property(modelParameter, property),
                modelParameter);

            var setPropertyMethod = typeof(NhSetPropertyCalls<TUpdateMutateModel>)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(method =>
                    method.Name == nameof(NhSetPropertyCalls<TUpdateMutateModel>.SetProperty) &&
                    method.IsGenericMethodDefinition);

            var call = Expression.Call(
                callsParameter,
                setPropertyMethod.MakeGenericMethod(property.PropertyType),
                Expression.Quote(selector),
                Expression.Convert(valueParameter, property.PropertyType));

            return Expression.Lambda<
                Func<NhSetPropertyCalls<TUpdateMutateModel>, object?, NhSetPropertyCalls<TUpdateMutateModel>>>(
                    call,
                    callsParameter,
                    valueParameter)
                .Compile();
        }
    }
}

internal sealed class NhPartialUpdateMapping<TUpdateMutateModel>(
    IReadOnlyList<NhPartialUpdateAssignment<TUpdateMutateModel>> assignments,
    IReadOnlyList<NhPartialUpdateMappingError> errors)
    where TUpdateMutateModel : class
{
    public IReadOnlyList<NhPartialUpdateMappingError> Errors { get; } = errors;

    public bool HasChanges => assignments.Count > 0;

    public NhSetPropertyCalls<TUpdateMutateModel> Apply(NhSetPropertyCalls<TUpdateMutateModel> calls)
    {
        foreach (var assignment in assignments)
        {
            calls = assignment.Setter(calls, assignment.Value);
        }

        return calls;
    }
}

internal sealed record NhPartialUpdateMappingError(string PropertyName, string Message);

internal sealed record NhPartialUpdateAssignment<TUpdateMutateModel>(
    Func<NhSetPropertyCalls<TUpdateMutateModel>, object?, NhSetPropertyCalls<TUpdateMutateModel>> Setter,
    object? Value)
    where TUpdateMutateModel : class;
