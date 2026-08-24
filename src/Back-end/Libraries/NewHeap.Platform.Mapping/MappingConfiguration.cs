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

    protected Dictionary<string, ConfiguredMemberMap> ConfiguredMembers { get; } =
        new(StringComparer.Ordinal);

    protected Func<object, object, object?, object?, bool>? AllMembersCondition { get; set; }

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

        var memberMaps = new List<MemberMapDefinition>();
        foreach (var destinationProperty in destinationProperties.Values)
        {
            ConfiguredMembers.TryGetValue(destinationProperty.Name, out var configuredMember);
            sourceProperties.TryGetValue(destinationProperty.Name, out var sourceProperty);

            if (configuredMember?.SourceResolver is null && sourceProperty is null)
            {
                continue;
            }

            memberMaps.Add(new MemberMapDefinition(
                destinationProperty,
                configuredMember?.SourceResolver ?? (source => sourceProperty!.GetValue(source)),
                configuredMember?.SourceValueType ?? sourceProperty!.PropertyType,
                configuredMember?.Condition ?? AllMembersCondition));
        }

        MemberMaps = memberMaps;
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

    public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        var compiled = sourceMember.Compile();
        _configuredMember.SourceResolver = source => compiled((TSource)source);
        _configuredMember.SourceValueType = typeof(TSourceMember);
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
    public Func<object, object?>? SourceResolver { get; set; }
    public Type? SourceValueType { get; set; }
    public Func<object, object, object?, object?, bool>? Condition { get; set; }
}

internal sealed record MemberMapDefinition(
    PropertyInfo DestinationProperty,
    Func<object, object?> SourceResolver,
    Type SourceValueType,
    Func<object, object, object?, object?, bool>? Condition);
