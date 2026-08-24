using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Mapping;

public interface IConfigurationProvider
{
    IMapper CreateMapper();
    IMapper CreateMapper(Func<Type, object?> serviceFactory);
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
}

public interface IMemberConfigurationExpression<TSource, TDestination, TDestinationMember>
{
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);

    void Condition(
        Func<TSource, TDestination, object?, object?, bool> condition);
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
