using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Mapping;

public abstract class Profile
{
    private readonly List<TypeMapDefinition> _typeMaps = [];

    protected Profile()
        : this(null)
    {
    }

    protected Profile(string? profileName)
    {
        ProfileName = string.IsNullOrWhiteSpace(profileName)
            ? GetType().FullName ?? GetType().Name
            : profileName;
    }

    public string ProfileName { get; }

    internal IReadOnlyList<TypeMapDefinition> TypeMaps => _typeMaps;

    protected IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var typeMap = new TypeMapDefinition<TSource, TDestination>(ProfileName);
        _typeMaps.Add(typeMap);
        return typeMap;
    }
}

public sealed class MapperConfiguration : IConfigurationProvider
{
    internal const int DefaultMaxDepth = 64;

    private readonly IReadOnlyDictionary<TypePair, TypeMapDefinition> _typeMaps;

    public MapperConfiguration(Action<IMapperConfigurationExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var expression = new MapperConfigurationExpression();
        configure(expression);
        _typeMaps = expression.Build();
    }

    public IMapper CreateMapper() => new Mapper(this);

    public IMapper CreateMapper(Func<Type, object?> serviceFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceFactory);
        return new Mapper(this, serviceFactory);
    }

    public void AssertConfigurationIsValid()
    {
        var errors = new List<string>();

        foreach (var typeMap in _typeMaps.Values
                     .OrderBy(map => map.SourceType.FullName, StringComparer.Ordinal)
                     .ThenBy(map => map.DestinationType.FullName, StringComparer.Ordinal))
        {
            if (typeMap.UsesTypeConverter)
            {
                continue;
            }

            if (!typeMap.HasDestinationConstructor && !CanCreateDestination(typeMap.DestinationType))
            {
                errors.Add(
                    $"Map from '{typeMap.SourceType.FullName}' to '{typeMap.DestinationType.FullName}' " +
                    "requires ConstructUsing or a parameterless destination constructor.");
            }

            foreach (var unmappedMember in typeMap.UnmappedDestinationMembers)
            {
                errors.Add(
                    $"Destination member '{typeMap.DestinationType.FullName}.{unmappedMember}' is unmapped. " +
                    "Configure MapFrom or Ignore explicitly.");
            }

            foreach (var memberMap in typeMap.MemberMaps)
            {
                if (CanMapType(memberMap.SourceValueType, memberMap.DestinationProperty.PropertyType, []))
                {
                    continue;
                }

                errors.Add(
                    $"Destination member '{typeMap.DestinationType.FullName}." +
                    $"{memberMap.DestinationProperty.Name}' cannot map from " +
                    $"'{memberMap.SourceValueType.FullName}' to " +
                    $"'{memberMap.DestinationProperty.PropertyType.FullName}'.");
            }
        }

        if (errors.Count > 0)
        {
            throw new MappingConfigurationException(
                "Mapping configuration is invalid:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }
    }

    internal TypeMapDefinition? FindTypeMap(Type sourceType, Type destinationType)
    {
        if (_typeMaps.TryGetValue(new TypePair(sourceType, destinationType), out var exact))
        {
            return exact;
        }

        return _typeMaps.Values
            .Where(typeMap =>
                typeMap.SourceType.IsAssignableFrom(sourceType) &&
                destinationType == typeMap.DestinationType)
            .OrderByDescending(typeMap => GetInheritanceDepth(typeMap.SourceType))
            .FirstOrDefault();
    }

    private static int GetInheritanceDepth(Type type)
    {
        var depth = 0;
        for (var current = type; current.BaseType is not null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private bool CanMapType(Type sourceType, Type destinationType, HashSet<TypePair> visited)
    {
        var pair = new TypePair(sourceType, destinationType);
        if (!visited.Add(pair))
        {
            return true;
        }

        if (sourceType == typeof(object) || destinationType.IsAssignableFrom(sourceType))
        {
            return true;
        }

        var nullableSourceType = Nullable.GetUnderlyingType(sourceType);
        if (nullableSourceType is not null && CanMapType(nullableSourceType, destinationType, visited))
        {
            return true;
        }

        var nullableDestinationType = Nullable.GetUnderlyingType(destinationType);
        if (nullableDestinationType is not null && CanMapType(sourceType, nullableDestinationType, visited))
        {
            return true;
        }

        if (TryGetCollectionElementType(sourceType, out var sourceElementType) &&
            TryGetCollectionElementType(destinationType, out var destinationElementType))
        {
            return CanMapType(sourceElementType, destinationElementType, visited);
        }

        if (destinationType.IsEnum && (sourceType == typeof(string) || sourceType.IsEnum || IsNumeric(sourceType)))
        {
            return true;
        }

        if (typeof(IConvertible).IsAssignableFrom(sourceType) &&
            typeof(IConvertible).IsAssignableFrom(destinationType))
        {
            return true;
        }

        return FindTypeMap(sourceType, destinationType) is not null;
    }

    private static bool CanCreateDestination(Type destinationType)
    {
        if (destinationType.IsValueType)
        {
            return true;
        }

        return !destinationType.IsAbstract &&
               !destinationType.IsInterface &&
               destinationType.GetConstructor(
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   Type.EmptyTypes,
                   modifiers: null) is not null;
    }

    private static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = null!;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerableType = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableType is null)
        {
            elementType = null!;
            return false;
        }

        elementType = enumerableType.GetGenericArguments()[0];
        return true;
    }

    private static bool IsNumeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong);
    }
}

internal sealed class MapperConfigurationExpression : IMapperConfigurationExpression
{
    private readonly List<TypeMapDefinition> _typeMaps = [];
    private readonly HashSet<Type> _profileTypes = [];

    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
    {
        var typeMap = new TypeMapDefinition<TSource, TDestination>("Configuration");
        _typeMaps.Add(typeMap);
        return typeMap;
    }

    public void AddProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!_profileTypes.Add(profile.GetType()))
        {
            return;
        }

        _typeMaps.AddRange(profile.TypeMaps);
    }

    public void AddProfile<TProfile>() where TProfile : Profile, new()
        => AddProfile(new TProfile());

    public void AddMaps(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        foreach (var assembly in assemblies.Distinct())
        {
            ArgumentNullException.ThrowIfNull(assembly);

            foreach (var profileType in GetLoadableTypes(assembly)
                         .Where(type =>
                             typeof(Profile).IsAssignableFrom(type) &&
                             !type.IsAbstract &&
                             type.GetConstructor(
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                 binder: null,
                                 Type.EmptyTypes,
                                 modifiers: null) is not null)
                         .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (_profileTypes.Contains(profileType))
                {
                    continue;
                }

                if (Activator.CreateInstance(profileType, nonPublic: true) is not Profile profile)
                {
                    throw new MappingConfigurationException(
                        $"Profile '{profileType.FullName}' could not be constructed.");
                }

                AddProfile(profile);
            }
        }
    }

    public void AddMaps(params Type[] markerTypes)
    {
        ArgumentNullException.ThrowIfNull(markerTypes);
        AddMaps(markerTypes.Select(type => type.Assembly).Distinct().ToArray());
    }

    internal IReadOnlyDictionary<TypePair, TypeMapDefinition> Build()
    {
        var result = new Dictionary<TypePair, TypeMapDefinition>();

        foreach (var typeMap in _typeMaps)
        {
            typeMap.Seal();
            var key = new TypePair(typeMap.SourceType, typeMap.DestinationType);

            if (!result.TryAdd(key, typeMap))
            {
                throw new MappingConfigurationException(
                    $"A map from '{typeMap.SourceType.FullName}' to " +
                    $"'{typeMap.DestinationType.FullName}' is registered more than once.");
            }
        }

        return result;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }
}

internal readonly record struct TypePair(Type SourceType, Type DestinationType);

internal abstract class TypeMapDefinition
{
    private bool _isSealed;

    protected TypeMapDefinition(Type sourceType, Type destinationType, string profileName)
    {
        SourceType = sourceType;
        DestinationType = destinationType;
        ProfileName = profileName;
    }

    public Type SourceType { get; }
    public Type DestinationType { get; }
    public string ProfileName { get; }
    public int MaximumDepth { get; protected set; } = MapperConfiguration.DefaultMaxDepth;
    public IReadOnlyList<MemberMapDefinition> MemberMaps { get; private set; } = [];
    public IReadOnlyList<string> UnmappedDestinationMembers { get; private set; } = [];
    public bool UsesTypeConverter => TypeConverter is not null;
    public bool HasDestinationConstructor => DestinationConstructor is not null;
    public MappingTypeConverter? TypeConverter { get; protected set; }
    public MappingDestinationConstructor? DestinationConstructor { get; protected set; }
    public IReadOnlyList<MappingAfterAction> AfterMapActions { get; protected set; } = [];

    protected Dictionary<string, ConfiguredMemberMap> ConfiguredMembers { get; } =
        new(StringComparer.Ordinal);

    protected Func<object, object, object?, object?, bool>? AllMembersCondition { get; set; }
    protected bool AllMembersIgnored { get; set; }

    protected void ThrowIfSealed()
    {
        if (_isSealed)
        {
            throw new MappingConfigurationException(
                $"The map from '{SourceType.FullName}' to '{DestinationType.FullName}' is already sealed.");
        }
    }

    internal void Seal()
    {
        if (_isSealed)
        {
            return;
        }

        var sourceProperties = SourceType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.GetMethod?.IsPublic == true &&
                property.GetIndexParameters().Length == 0)
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var destinationProperties = DestinationType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.SetMethod?.IsPublic == true &&
                property.GetIndexParameters().Length == 0)
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var configuredMemberName in ConfiguredMembers.Keys)
        {
            if (!destinationProperties.ContainsKey(configuredMemberName))
            {
                throw new MappingConfigurationException(
                    $"Destination member '{configuredMemberName}' does not exist on " +
                    $"'{DestinationType.FullName}'.");
            }
        }

        if (TypeConverter is not null)
        {
            MemberMaps = [];
            UnmappedDestinationMembers = [];
            _isSealed = true;
            return;
        }

        var memberMaps = new List<MemberMapDefinition>();
        var unmappedDestinationMembers = new List<string>();
        foreach (var destinationProperty in destinationProperties.Values)
        {
            ConfiguredMembers.TryGetValue(destinationProperty.Name, out var configuredMember);
            sourceProperties.TryGetValue(destinationProperty.Name, out var sourceProperty);

            if (AllMembersIgnored || configuredMember?.IsIgnored == true)
            {
                continue;
            }

            if (configuredMember?.SourceResolver is null && sourceProperty is null)
            {
                unmappedDestinationMembers.Add(destinationProperty.Name);
                continue;
            }

            memberMaps.Add(new MemberMapDefinition(
                destinationProperty,
                configuredMember?.SourceResolver ??
                ((source, _, _, _) => sourceProperty!.GetValue(source)),
                configuredMember?.SourceValueType ?? sourceProperty!.PropertyType,
                configuredMember?.Condition ?? AllMembersCondition));
        }

        MemberMaps = memberMaps;
        UnmappedDestinationMembers = unmappedDestinationMembers;
        _isSealed = true;
    }
}

internal sealed class TypeMapDefinition<TSource, TDestination> :
    TypeMapDefinition,
    IMappingExpression<TSource, TDestination>
{
    public TypeMapDefinition(string profileName)
        : base(typeof(TSource), typeof(TDestination), profileName)
    {
    }

    public IMappingExpression<TSource, TDestination> ForMember<TDestinationMember>(
        Expression<Func<TDestination, TDestinationMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TDestinationMember>> memberOptions)
    {
        ThrowIfSealed();
        ArgumentNullException.ThrowIfNull(destinationMember);
        ArgumentNullException.ThrowIfNull(memberOptions);

        var property = GetDirectProperty(destinationMember);
        if (!ConfiguredMembers.TryGetValue(property.Name, out var configuredMember))
        {
            configuredMember = new ConfiguredMemberMap();
            ConfiguredMembers[property.Name] = configuredMember;
        }

        memberOptions(new MemberConfigurationExpression<TSource, TDestination, TDestinationMember>(configuredMember));
        return this;
    }

    public void ForAllMembers(
        Action<IMemberConfigurationExpression<TSource, TDestination, object?>> memberOptions)
    {
        ThrowIfSealed();
        ArgumentNullException.ThrowIfNull(memberOptions);

        var configuredMember = new ConfiguredMemberMap();
        memberOptions(new MemberConfigurationExpression<TSource, TDestination, object?>(configuredMember));
        AllMembersCondition = configuredMember.Condition;
        AllMembersIgnored = configuredMember.IsIgnored;
    }

    public IMappingExpression<TSource, TDestination> MaxDepth(int depth)
    {
        ThrowIfSealed();
        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "Mapping depth must be greater than zero.");
        }

        MaximumDepth = depth;
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConvertUsing(
        Func<TSource, TDestination> converter)
    {
        ThrowIfSealed();
        ArgumentNullException.ThrowIfNull(converter);

        TypeConverter = (source, _, _) => converter((TSource)source);
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConvertUsing<TConverter>()
        where TConverter : ITypeConverter<TSource, TDestination>
    {
        ThrowIfSealed();

        TypeConverter = (source, destination, context) =>
        {
            var converter = (TConverter)context.GetRequiredService(typeof(TConverter));
            var typedDestination = destination is null ? default! : (TDestination)destination;
            return converter.Convert(
                (TSource)source,
                typedDestination,
                context.ResolutionContext);
        };
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConstructUsing(
        Func<TSource, TDestination> constructor)
    {
        ThrowIfSealed();
        ArgumentNullException.ThrowIfNull(constructor);

        DestinationConstructor = (source, _) => constructor((TSource)source);
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(
        Action<TSource, TDestination> action)
    {
        ThrowIfSealed();
        ArgumentNullException.ThrowIfNull(action);

        AfterMapActions = AfterMapActions
            .Append<MappingAfterAction>((source, destination, _) =>
                action((TSource)source, (TDestination)destination))
            .ToArray();
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>
    {
        ThrowIfSealed();

        AfterMapActions = AfterMapActions
            .Append<MappingAfterAction>((source, destination, context) =>
            {
                var action = (TAction)context.GetRequiredService(typeof(TAction));
                action.Process(
                    (TSource)source,
                    (TDestination)destination,
                    context.ResolutionContext);
            })
            .ToArray();
        return this;
    }

    private static PropertyInfo GetDirectProperty<TDestinationMember>(
        Expression<Func<TDestination, TDestinationMember>> destinationMember)
    {
        var body = destinationMember.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary
            ? unary.Operand
            : destinationMember.Body;

        if (body is not MemberExpression { Member: PropertyInfo property } memberExpression ||
            memberExpression.Expression != destinationMember.Parameters[0])
        {
            throw new ArgumentException(
                "A destination member must select one direct property.",
                nameof(destinationMember));
        }

        return property;
    }
}

internal sealed class MemberConfigurationExpression<TSource, TDestination, TDestinationMember> :
    IMemberConfigurationExpression<TSource, TDestination, TDestinationMember>
{
    private readonly ConfiguredMemberMap _configuredMember;

    public MemberConfigurationExpression(ConfiguredMemberMap configuredMember)
    {
        _configuredMember = configuredMember;
    }

    public void Ignore()
    {
        _configuredMember.IsIgnored = true;
        _configuredMember.SourceResolver = null;
        _configuredMember.SourceValueType = null;
    }

    public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        var compiled = sourceMember.Compile();
        _configuredMember.IsIgnored = false;
        _configuredMember.SourceResolver = (source, _, _, _) => compiled((TSource)source);
        _configuredMember.SourceValueType = typeof(TSourceMember);
    }

    public void MapFrom<TResolver>()
        where TResolver : IValueResolver<TSource, TDestination, TDestinationMember>
    {
        _configuredMember.IsIgnored = false;
        _configuredMember.SourceResolver = (source, destination, destinationMember, context) =>
        {
            var resolver = (TResolver)context.GetRequiredService(typeof(TResolver));
            var typedDestinationMember = destinationMember is null
                ? default!
                : (TDestinationMember)destinationMember;
            return resolver.Resolve(
                (TSource)source,
                (TDestination)destination,
                typedDestinationMember,
                context.ResolutionContext);
        };
        _configuredMember.SourceValueType = typeof(TDestinationMember);
    }

    public void Condition(Func<TSource, TDestination, object?, object?, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _configuredMember.Condition = (source, destination, sourceMember, destinationMember) =>
            condition((TSource)source, (TDestination)destination, sourceMember, destinationMember);
    }
}

internal sealed class ConfiguredMemberMap
{
    public bool IsIgnored { get; set; }
    public MappingMemberResolver? SourceResolver { get; set; }
    public Type? SourceValueType { get; set; }
    public Func<object, object, object?, object?, bool>? Condition { get; set; }
}

internal sealed record MemberMapDefinition(
    PropertyInfo DestinationProperty,
    MappingMemberResolver SourceResolver,
    Type SourceValueType,
    Func<object, object, object?, object?, bool>? Condition);

internal delegate object? MappingMemberResolver(
    object source,
    object destination,
    object? destinationMember,
    MappingOperationContext context);

internal delegate object? MappingTypeConverter(
    object source,
    object? destination,
    MappingOperationContext context);

internal delegate object? MappingDestinationConstructor(
    object source,
    MappingOperationContext context);

internal delegate void MappingAfterAction(
    object source,
    object destination,
    MappingOperationContext context);

internal sealed class MappingOperationContext
{
    private readonly Func<Type, object?> _serviceFactory;

    public MappingOperationContext(IMapper mapper, Func<Type, object?> serviceFactory)
    {
        ResolutionContext = new ResolutionContext(mapper);
        _serviceFactory = serviceFactory;
    }

    public ResolutionContext ResolutionContext { get; }

    public object GetRequiredService(Type serviceType)
    {
        var service = _serviceFactory(serviceType);
        if (service is null)
        {
            throw new MappingException(
                $"The mapping service factory could not resolve '{serviceType.FullName}'.");
        }

        return service;
    }
}
