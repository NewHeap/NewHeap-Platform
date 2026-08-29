using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Mapping;

public interface IConfigurationProvider
{
    IMapper CreateMapper();
    IMapper CreateMapper(Func<Type, object?> serviceFactory);
    void AssertConfigurationIsValid();
}

public interface IMapper
{
    IConfigurationProvider ConfigurationProvider { get; }

    TDestination Map<TDestination>(object? source);

    TDestination Map<TSource, TDestination>(TSource source);

    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);

    object? Map(object? source, Type sourceType, Type destinationType);

    object? Map(object? source, object? destination, Type sourceType, Type destinationType);
}

public interface IMapperConfigurationExpression
{
    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();

    void AddProfile(Profile profile);

    void AddProfile<TProfile>() where TProfile : Profile, new();

    void AddMaps(params Assembly[] assemblies);

    void AddMaps(params Type[] markerTypes);
}

public interface IMappingExpression<TSource, TDestination>
{
    IMappingExpression<TSource, TDestination> ForMember<TDestinationMember>(
        Expression<Func<TDestination, TDestinationMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TDestinationMember>> memberOptions);

    void ForAllMembers(
        Action<IMemberConfigurationExpression<TSource, TDestination, object?>> memberOptions);

    IMappingExpression<TSource, TDestination> MaxDepth(int depth);

    IMappingExpression<TSource, TDestination> IncludeBase<TSourceBase, TDestinationBase>();

    IMappingExpression<TSource, TDestination> ConvertUsing(
        Func<TSource, TDestination> converter);

    IMappingExpression<TSource, TDestination> ConvertUsing<TConverter>()
        where TConverter : ITypeConverter<TSource, TDestination>;

    IMappingExpression<TSource, TDestination> ConstructUsing(
        Func<TSource, TDestination> constructor);

    IMappingExpression<TSource, TDestination> AfterMap(
        Action<TSource, TDestination> action);

    IMappingExpression<TSource, TDestination> AfterMap<TAction>()
        where TAction : IMappingAction<TSource, TDestination>;
}

public interface IMemberConfigurationExpression<TSource, TDestination, TDestinationMember>
{
    void Ignore();

    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);

    void MapFrom<TResolver>()
        where TResolver : IValueResolver<TSource, TDestination, TDestinationMember>;

    void Condition(
        Func<TSource, TDestination, object?, object?, bool> condition);
}

public interface IValueResolver<in TSource, in TDestination, TDestinationMember>
{
    TDestinationMember Resolve(
        TSource source,
        TDestination destination,
        TDestinationMember destinationMember,
        ResolutionContext context);
}

public interface ITypeConverter<in TSource, TDestination>
{
    TDestination Convert(
        TSource source,
        TDestination destination,
        ResolutionContext context);
}

public interface IMappingAction<in TSource, in TDestination>
{
    void Process(
        TSource source,
        TDestination destination,
        ResolutionContext context);
}

public sealed class ResolutionContext
{
    internal ResolutionContext(IMapper mapper)
    {
        Mapper = mapper;
    }

    public IMapper Mapper { get; }
}

public class MappingException : InvalidOperationException
{
    public MappingException(string message)
        : base(message)
    {
    }

    public MappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MappingConfigurationException : MappingException
{
    public MappingConfigurationException(string message)
        : base(message)
    {
    }
}
