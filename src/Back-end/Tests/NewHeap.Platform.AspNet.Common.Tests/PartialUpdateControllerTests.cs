using NewHeap.Platform.Mapping;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Controllers;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NSubstitute;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class PartialUpdateControllerTests
{
    [Fact]
    public void MapperAppliesOnlyPresentValuesIncludingNullAndFalse()
    {
        var originalCountryId = Guid.NewGuid();
        var model = new TestMutateModel
        {
            CountryId = originalCountryId,
            PostalCode = "before",
            IsCompany = true,
            VatNumber = "NL123",
            Status = TestStatus.Draft
        };
        var patch = JObject.Parse(
            """
            {
              "countryId": null,
              "postal-code": "1234 AB",
              "isCompany": false,
              "status": "active"
            }
            """);

        var mapping = NhPartialUpdateMapper.Map<TestMutateModel>(
            patch,
            CreateSerializer(),
            _ => true);

        Assert.Empty(mapping.Errors);
        Assert.True(mapping.HasChanges);

        mapping.Apply(new NhSetPropertyCalls<TestMutateModel>()).Apply(model);

        Assert.Null(model.CountryId);
        Assert.Equal("1234 AB", model.PostalCode);
        Assert.False(model.IsCompany);
        Assert.Equal("NL123", model.VatNumber);
        Assert.Equal(TestStatus.Active, model.Status);
    }

    [Fact]
    public void MapperRejectsUnknownForbiddenDuplicateAndInvalidValues()
    {
        var patch = JObject.Parse(
            """
            {
              "countryId": 42,
              "isCompany": true,
              "IsCompany": false,
              "unknown": "value"
            }
            """);

        var mapping = NhPartialUpdateMapper.Map<TestMutateModel>(
            patch,
            CreateSerializer(),
            propertyName => propertyName != nameof(TestMutateModel.IsCompany));

        Assert.False(mapping.HasChanges);
        Assert.Collection(
            mapping.Errors,
            error => Assert.Equal("countryId", error.PropertyName),
            error => Assert.Equal("isCompany", error.PropertyName),
            error => Assert.Equal("IsCompany", error.PropertyName),
            error => Assert.Equal("unknown", error.PropertyName));
    }

    [Fact]
    public void MapperRejectsNullForNonNullableValueType()
    {
        var mapping = NhPartialUpdateMapper.Map<TestMutateModel>(
            JObject.Parse("""{ "isCompany": null }"""),
            CreateSerializer(),
            _ => true);

        var error = Assert.Single(mapping.Errors);
        Assert.Equal("isCompany", error.PropertyName);
        Assert.Equal("Invalid partial-update value", error.Message);
    }

    [Fact]
    public void MapperUsesPropertyLevelJsonConverter()
    {
        var mapping = NhPartialUpdateMapper.Map<TestMutateModel>(
            JObject.Parse("""{ "convertedValue": "mixed Case" }"""),
            CreateSerializer(),
            _ => true);
        var model = new TestMutateModel();

        mapping.Apply(new NhSetPropertyCalls<TestMutateModel>()).Apply(model);

        Assert.Empty(mapping.Errors);
        Assert.Equal("MIXED CASE", model.ConvertedValue);
    }

    [Fact]
    public void PublicControllerAppliesPatchToExistingModelAndValidatesCompleteResult()
    {
        var controller = CreatePublicController();
        var model = new ExistingModelMutateModel
        {
            RequiredValue = "required",
            OptionalValue = "clear me",
            Enabled = true,
            Count = 12,
            DisplayName = "before"
        };

        var success = controller.ApplyPartialUpdate(
            model,
            JObject.Parse(
                """
                {
                  "optionalValue": null,
                  "enabled": false,
                  "count": 0,
                  "display-name": "after"
                }
                """));

        Assert.True(success);
        Assert.Equal("required", model.RequiredValue);
        Assert.Null(model.OptionalValue);
        Assert.False(model.Enabled);
        Assert.Equal(0, model.Count);
        Assert.Equal("after", model.DisplayName);
    }

    [Fact]
    public void PublicControllerRejectsEntirePatchBeforeMutatingExistingModel()
    {
        var controller = CreatePublicController();
        var model = new ExistingModelMutateModel
        {
            RequiredValue = "required",
            Enabled = true,
            DisplayName = "before"
        };

        var success = controller.ApplyPartialUpdate(
            model,
            JObject.Parse("""{ "display-name": "after", "enabled": false }"""),
            propertyName => propertyName != nameof(ExistingModelMutateModel.Enabled));

        Assert.False(success);
        Assert.Equal("before", model.DisplayName);
        Assert.True(model.Enabled);
        Assert.True(controller.ModelState.ContainsKey("enabled"));
    }

    [Fact]
    public void PublicControllerValidatesCompleteModelAfterApplyingPatch()
    {
        var controller = CreatePublicController();
        var model = new ExistingModelMutateModel
        {
            RequiredValue = "required"
        };

        var success = controller.ApplyPartialUpdate(
            model,
            JObject.Parse("""{ "requiredValue": null }"""));

        Assert.False(success);
        Assert.Null(model.RequiredValue);
        Assert.True(controller.ModelState.ContainsKey(nameof(ExistingModelMutateModel.RequiredValue)));
    }

    [Fact]
    public async Task DbEntityControllerMapsPatchAndReturnsNoContent()
    {
        var service = new FakeDbEntityService();
        var controller = CreateDbEntityController(service);
        var id = Guid.NewGuid();
        using var cancellationTokenSource = new CancellationTokenSource();

        var actionResult = await controller.PartialUpdate(
            id,
            JObject.Parse("""{ "postal-code": "after", "isCompany": false }"""),
            cancellationTokenSource.Token);

        Assert.IsType<NoContentResult>(actionResult);
        Assert.Equal(id, service.PartialUpdateId);
        Assert.Equal(cancellationTokenSource.Token, service.PartialUpdateCancellationToken);
        Assert.NotNull(service.PartialUpdateSetters);

        var model = new TestMutateModel
        {
            PostalCode = "before",
            IsCompany = true,
            VatNumber = "unchanged"
        };
        service.PartialUpdateSetters!(new NhSetPropertyCalls<TestMutateModel>()).Apply(model);

        Assert.Equal("after", model.PostalCode);
        Assert.False(model.IsCompany);
        Assert.Equal("unchanged", model.VatNumber);
    }

    [Fact]
    public async Task DbEntityControllerReturnsBadRequestWithoutCallingServiceForInvalidPatch()
    {
        var service = new FakeDbEntityService();
        var controller = CreateDbEntityController(service);

        var actionResult = await controller.PartialUpdate(
            Guid.NewGuid(),
            JObject.Parse("""{ "countryId": 42, "notPatchable": true }"""));

        Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Null(service.PartialUpdateSetters);
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey("countryId"));
        Assert.True(controller.ModelState.ContainsKey("notPatchable"));
    }

    [Fact]
    public async Task DbEntityControllerMapsServiceErrorsToBadRequest()
    {
        var service = new FakeDbEntityService
        {
            PartialUpdateResult = TaskResult<TestEntity?>.Failed(
                nameof(TestMutateModel.PostalCode),
                "Postal code was rejected")
        };
        var controller = CreateDbEntityController(service);

        var actionResult = await controller.PartialUpdate(
            Guid.NewGuid(),
            JObject.Parse("""{ "postal-code": "after" }"""));

        Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.True(controller.ModelState.ContainsKey(nameof(TestMutateModel.PostalCode)));
    }

    [Fact]
    public async Task CompositeControllerUsesCompositePartialUpdateService()
    {
        var service = new FakeCompositeDbEntityService();
        var controller = CreateCompositeController(service);
        var id = Guid.NewGuid();

        var actionResult = await controller.PartialUpdate(
            id,
            JObject.Parse("""{ "vatNumber": null }"""));

        Assert.IsType<NoContentResult>(actionResult);
        Assert.Equal(id, service.PartialUpdateId);
        Assert.NotNull(service.PartialUpdateSetters);

        var model = new TestMutateModel { VatNumber = "NL123" };
        service.PartialUpdateSetters!(new NhSetPropertyCalls<TestMutateModel>()).Apply(model);
        Assert.Null(model.VatNumber);
    }

    [Fact]
    public async Task EmptyPatchIsNoOpAndDoesNotCallService()
    {
        var service = new FakeDbEntityService();
        var controller = CreateDbEntityController(service);

        var actionResult = await controller.PartialUpdate(Guid.NewGuid(), new JObject());

        Assert.IsType<NoContentResult>(actionResult);
        Assert.Null(service.PartialUpdateSetters);
    }

    [Fact]
    public async Task NullPatchReturnsBadRequestWithoutCallingService()
    {
        var service = new FakeDbEntityService();
        var controller = CreateDbEntityController(service);

        var actionResult = await controller.PartialUpdate(Guid.NewGuid(), null);

        Assert.IsType<BadRequestObjectResult>(actionResult);
        Assert.Null(service.PartialUpdateSetters);
        Assert.False(controller.ModelState.IsValid);
    }

    private static JsonSerializer CreateSerializer()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy()
            }
        };
        settings.Converters.Add(new StringEnumConverter());
        return JsonSerializer.Create(settings);
    }

    private static TestDbEntityController CreateDbEntityController(FakeDbEntityService service)
    {
        var controller = new TestDbEntityController(
            Substitute.For<IMapper>(),
            Substitute.For<ILogger>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<IStringLocalizer>(),
            Substitute.For<IHttpCollectionProcessingService>(),
            service);
        SetHttpContext(controller);
        return controller;
    }

    private static TestPublicController CreatePublicController()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddControllers()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy()
                };
                options.SerializerSettings.Converters.Add(new StringEnumConverter());
            });

        var controller = new TestPublicController(
            Substitute.For<IMapper>(),
            Substitute.For<ILogger>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<IStringLocalizer>(),
            Substitute.For<IHttpCollectionProcessingService>());
        SetHttpContext(controller, services.BuildServiceProvider());
        return controller;
    }

    private static TestCompositeController CreateCompositeController(FakeCompositeDbEntityService service)
    {
        var controller = new TestCompositeController(
            Substitute.For<IMapper>(),
            Substitute.For<ILogger>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<IStringLocalizer>(),
            Substitute.For<IHttpCollectionProcessingService>(),
            service);
        SetHttpContext(controller);
        return controller;
    }

    private static void SetHttpContext(
        ControllerBase controller,
        IServiceProvider? requestServices = null)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = requestServices ?? new ServiceCollection().BuildServiceProvider()
            }
        };
    }

    private sealed class TestPublicController(
        IMapper mapper,
        ILogger logger,
        IConfiguration configuration,
        IStringLocalizer localizer,
        IHttpCollectionProcessingService collectionProcessingService)
        : PublicNhBaseController(
            mapper,
            logger,
            configuration,
            localizer,
            collectionProcessingService)
    {
        public bool ApplyPartialUpdate<TModel>(
            TModel target,
            JObject? partialUpdate,
            Func<string, bool>? canPartiallyUpdateProperty = null)
            where TModel : class
        {
            return TryApplyPartialUpdate(
                target,
                partialUpdate,
                canPartiallyUpdateProperty);
        }
    }

    private sealed class TestDbEntityController(
        IMapper mapper,
        ILogger logger,
        IConfiguration configuration,
        IStringLocalizer localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        FakeDbEntityService service)
        : DbEntityProtectedNhBaseController<
            TestEntity,
            TestMutateModel,
            TestViewModel,
            FakeDbEntityService,
            CollectionRequestModel>(
                mapper,
                logger,
                configuration,
                localizer,
                collectionProcessingService,
                service)
    {
        public Task<IActionResult> PartialUpdate(
            Guid id,
            JObject? partialUpdate,
            CancellationToken cancellationToken = default)
        {
            return DoUpdatePartial(id, partialUpdate, cancellationToken);
        }
    }

    private sealed class TestCompositeController(
        IMapper mapper,
        ILogger logger,
        IConfiguration configuration,
        IStringLocalizer localizer,
        IHttpCollectionProcessingService collectionProcessingService,
        FakeCompositeDbEntityService service)
        : CompositeDbEntityProtectedNhBaseController<
            TestEntity,
            TestMutateModel,
            TestEntity,
            TestViewModel,
            FakeCompositeDbEntityService,
            CollectionRequestModel>(
                mapper,
                logger,
                configuration,
                localizer,
                collectionProcessingService,
                service)
    {
        public Task<IActionResult> PartialUpdate(
            Guid id,
            JObject? partialUpdate,
            CancellationToken cancellationToken = default)
        {
            return DoUpdatePartial(id, partialUpdate, cancellationToken);
        }
    }

    private sealed class FakeDbEntityService : IBaseDbEntityService<TestEntity, TestMutateModel>
    {
        private readonly IRepository<TestEntity> _repository = Substitute.For<IRepository<TestEntity>>();

        public Guid? PartialUpdateId { get; private set; }

        public CancellationToken PartialUpdateCancellationToken { get; private set; }

        public Func<NhSetPropertyCalls<TestMutateModel>, NhSetPropertyCalls<TestMutateModel>>? PartialUpdateSetters { get; private set; }

        public TaskResult<TestEntity?> PartialUpdateResult { get; init; } =
            TaskResult<TestEntity?>.Succeeded(new TestEntity());

        public IRepository<TestEntity> GetRepository() => _repository;

        public IQueryable<TestEntity> QueryableWithAllIncludes(IQueryable<TestEntity>? queryable = null) =>
            queryable ?? Array.Empty<TestEntity>().AsQueryable();

        public IQueryable<TestEntity> QueryableWithUpdateDeleteIncludes(IQueryable<TestEntity>? queryable = null) =>
            queryable ?? Array.Empty<TestEntity>().AsQueryable();

        public Task<TestEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskResult<TestEntity?>> CreateAsync(
            TestMutateModel mutateModel,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            BaseDbEntityServiceOperationOptions? options = null) =>
            throw new NotSupportedException();

        public Task<TaskResult<TestEntity?>> UpdateAsync(
            Guid id,
            TestMutateModel mutateModel,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            BaseDbEntityServiceOperationOptions? options = null) =>
            throw new NotSupportedException();

        public Task<TaskResult<TestEntity?>> UpdatePartialAsync(
            Guid id,
            Func<NhSetPropertyCalls<TestMutateModel>, NhSetPropertyCalls<TestMutateModel>> set,
            Action<NhSetPropertyCalls<TestMutateModel>>? callsReady = null,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            BaseDbEntityServiceOperationOptions? options = null)
        {
            PartialUpdateId = id;
            PartialUpdateCancellationToken = cancellationToken;
            PartialUpdateSetters = set;
            return Task.FromResult(PartialUpdateResult);
        }

        public Task<TaskResult<TestEntity?>> DeleteAsync(
            Guid id,
            Guid? committedByUserId = null,
            CancellationToken cancellationToken = default,
            BaseDbEntityServiceOperationOptions? options = null) =>
            throw new NotSupportedException();

        public Task<TaskResult<BulkCRUDResultModel<TestEntity>>> BulkAsync(
            BulkCRUDMutateModel<TestMutateModel, TestMutateModel, TestMutateModel> bulkCRUDMutateModel,
            BaseDbEntityServiceOperationOptions options,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            Action<NhSetPropertyCalls<TestMutateModel>>? partialUpdateCallsReady = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCompositeDbEntityService : ICompositeBaseDbEntityService<TestEntity, TestMutateModel, TestEntity>
    {
        private readonly IRepository<TestEntity> _repository = Substitute.For<IRepository<TestEntity>>();

        public Guid? PartialUpdateId { get; private set; }

        public Func<NhSetPropertyCalls<TestMutateModel>, NhSetPropertyCalls<TestMutateModel>>? PartialUpdateSetters { get; private set; }

        public IRepository<TestEntity> GetRepository() => _repository;

        public IQueryable<TestEntity> QueryableWithAllIncludes(IQueryable<TestEntity>? queryable = null) =>
            queryable ?? Array.Empty<TestEntity>().AsQueryable();

        public IQueryable<TestEntity> QueryableWithUpdateDeleteIncludes(IQueryable<TestEntity>? queryable = null) =>
            queryable ?? Array.Empty<TestEntity>().AsQueryable();

        public Task<TestEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskResult<TestEntity?>> CreateAsync(
            TestMutateModel mutateModel,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null) =>
            throw new NotSupportedException();

        public Task<TaskResult<TestEntity?>> UpdateAsync(
            Guid id,
            TestMutateModel mutateModel,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null) =>
            throw new NotSupportedException();

        public Task<TaskResult<TestEntity?>> UpdatePartialAsync(
            Guid id,
            Func<NhSetPropertyCalls<TestMutateModel>, NhSetPropertyCalls<TestMutateModel>> set,
            Action<NhSetPropertyCalls<TestMutateModel>>? callsReady = null,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null)
        {
            PartialUpdateId = id;
            PartialUpdateSetters = set;
            return Task.FromResult(TaskResult<TestEntity?>.Succeeded(new TestEntity()));
        }

        public Task<TaskResult<TestEntity?>> DeleteAsync(
            Guid id,
            Guid? committedByUserId = null,
            CancellationToken cancellationToken = default,
            CompositeBaseDbEntityServiceOperationOptions? options = null) =>
            throw new NotSupportedException();

        public Task<TaskResult<BulkCRUDResultModel<TestEntity>>> BulkAsync(
            BulkCRUDMutateModel<TestMutateModel, TestMutateModel, TestMutateModel> bulkCRUDMutateModel,
            CompositeBaseDbEntityServiceOperationOptions options,
            Guid? committedByUserId = null,
            Action<TestEntity>? beforeSave = null,
            CancellationToken cancellationToken = default,
            Action<NhSetPropertyCalls<TestMutateModel>>? partialUpdateCallsReady = null) =>
            throw new NotSupportedException();
    }

    public sealed class TestEntity : IdDbEntity
    {
        public Guid Id { get; set; }

        public DateTimeOffset CreationDateTime { get; set; }

        public DateTimeOffset LastModifiedDateTime { get; set; }
    }

    private sealed class TestMutateModel
    {
        public Guid? CountryId { get; set; }

        [JsonProperty("postal-code")]
        public string? PostalCode { get; set; }

        public bool IsCompany { get; set; }

        public string? VatNumber { get; set; }

        public TestStatus Status { get; set; }

        [JsonConverter(typeof(UppercaseStringJsonConverter))]
        public string? ConvertedValue { get; set; }
    }

    private sealed class ExistingModelMutateModel
    {
        [Required]
        public string? RequiredValue { get; set; }

        public string? OptionalValue { get; set; }

        public bool Enabled { get; set; }

        public int Count { get; set; }

        [JsonProperty("display-name")]
        public string? DisplayName { get; set; }
    }

    private sealed class TestViewModel
    {
    }

    private enum TestStatus
    {
        Draft,
        Active
    }

    private sealed class UppercaseStringJsonConverter : JsonConverter<string>
    {
        public override string? ReadJson(
            JsonReader reader,
            Type objectType,
            string? existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType != JsonToken.String)
            {
                throw new JsonSerializationException("Expected a string value.");
            }

            return ((string?)reader.Value)?.ToUpperInvariant();
        }

        public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer)
        {
            writer.WriteValue(value);
        }
    }
}
