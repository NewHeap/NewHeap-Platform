using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;

namespace NewHeap.Platform.Mapping;

public sealed class Mapper : IMapper
{
    private readonly MapperConfiguration _configuration;
    private readonly Func<Type, object?> _serviceFactory;

    public Mapper(IConfigurationProvider configurationProvider)
        : this(configurationProvider, _ => null)
    {
    }

    public Mapper(IConfigurationProvider configurationProvider, Func<Type, object?> serviceFactory)
    {
        ArgumentNullException.ThrowIfNull(configurationProvider);
        ArgumentNullException.ThrowIfNull(serviceFactory);

        _configuration = configurationProvider as MapperConfiguration
            ?? throw new ArgumentException(
                $"Configuration must be a {nameof(MapperConfiguration)} instance.",
                nameof(configurationProvider));
        _serviceFactory = serviceFactory;
    }

    public IConfigurationProvider ConfigurationProvider => _configuration;

    public TDestination Map<TDestination>(object? source)
        => (TDestination)MapCore(
            source,
            source?.GetType() ?? typeof(object),
            typeof(TDestination),
            destination: null,
            CreateMappingContext())!;

    public TDestination Map<TSource, TDestination>(TSource source)
        => (TDestination)MapCore(
            source,
            typeof(TSource),
            typeof(TDestination),
            destination: null,
            CreateMappingContext())!;

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
        => (TDestination)MapCore(
            source,
            typeof(TSource),
            typeof(TDestination),
            destination,
            CreateMappingContext())!;

    public object? Map(object? source, Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);
        return MapCore(source, sourceType, destinationType, destination: null, CreateMappingContext());
    }

    public object? Map(object? source, object? destination, Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);
        return MapCore(source, sourceType, destinationType, destination, CreateMappingContext());
    }

    private MappingContext CreateMappingContext()
    {
        var contextualMapper = new ContextualMapper(this);
        var context = new MappingContext(new MappingOperationContext(contextualMapper, _serviceFactory));
        contextualMapper.Attach(context);
        return context;
    }

    private object? MapCore(
        object? source,
        Type declaredSourceType,
        Type destinationType,
        object? destination,
        MappingContext context)
    {
        if (source is null)
        {
            if (TryGetDictionaryTypes(destinationType, out var nullDestinationKeyType, out var nullDestinationValueType))
            {
                return MapDictionary(
                    Array.Empty<object>(),
                    declaredSourceType,
                    destinationType,
                    nullDestinationKeyType,
                    nullDestinationValueType,
                    destination,
                    context);
            }

            if (TryGetCollectionElementType(destinationType, out var nullDestinationElementType))
            {
                return MapCollection(
                    Array.Empty<object>(),
                    declaredSourceType,
                    destinationType,
                    nullDestinationElementType,
                    destination,
                    context);
            }

            if (IsNonGenericListDestination(destinationType))
            {
                return MapNonGenericList(
                    Array.Empty<object>(),
                    declaredSourceType,
                    destinationType,
                    destination);
            }

            if (destination is not null)
            {
                return destination;
            }

            return CreateNullValue(destinationType);
        }

        var runtimeSourceType = source.GetType();
        var runtimeDestinationType = destination?.GetType() ?? destinationType;

        var typeMap = _configuration.FindTypeMap(runtimeSourceType, runtimeDestinationType)
            ?? _configuration.FindTypeMap(declaredSourceType, runtimeDestinationType)
            ?? _configuration.FindTypeMap(runtimeSourceType, destinationType)
            ?? _configuration.FindTypeMap(declaredSourceType, destinationType);

        if (typeMap is not null)
        {
            return MapWithTypeMap(source, destinationType, destination, context, typeMap);
        }

        if (destinationType.IsAssignableFrom(runtimeSourceType) &&
            !TryGetDictionaryTypes(destinationType, out _, out _) &&
            !IsSupportedCollectionDestination(destinationType))
        {
            return source;
        }

        if (TryGetKeyValuePairTypes(
                runtimeSourceType,
                out var sourceKeyType,
                out var sourceValueType) &&
            TryGetKeyValuePairTypes(
                destinationType,
                out var destinationPairKeyType,
                out var destinationPairValueType))
        {
            return MapKeyValuePair(
                source,
                sourceKeyType,
                sourceValueType,
                destinationType,
                destinationPairKeyType,
                destinationPairValueType,
                context);
        }

        if (TryGetDictionaryTypes(destinationType, out var destinationKeyType, out var destinationValueType) &&
            source is IEnumerable sourceDictionary)
        {
            return MapDictionary(
                sourceDictionary,
                declaredSourceType,
                destinationType,
                destinationKeyType,
                destinationValueType,
                destination,
                context);
        }

        if (TryGetCollectionElementType(destinationType, out var destinationElementType) &&
            source is IEnumerable sourceEnumerable &&
            source is not string)
        {
            return MapCollection(
                sourceEnumerable,
                declaredSourceType,
                destinationType,
                destinationElementType,
                destination,
                context);
        }

        if (IsNonGenericListDestination(destinationType) &&
            source is IEnumerable nonGenericSourceEnumerable &&
            source is not string)
        {
            return MapNonGenericList(
                nonGenericSourceEnumerable,
                declaredSourceType,
                destinationType,
                destination);
        }

        return ConvertWithoutTypeMap(source, destinationType, context);
    }

    private object? MapWithTypeMap(
        object source,
        Type destinationType,
        object? destination,
        MappingContext context,
        TypeMapDefinition typeMap)
    {
        var typePair = new TypePair(typeMap.SourceType, typeMap.DestinationType);
        if (!context.TryEnter(typePair, typeMap.MaximumDepth))
        {
            return CreateNullValue(destinationType);
        }

        try
        {
            if (typeMap.TypeConverter is not null)
            {
                object? convertedDestination;
                try
                {
                    convertedDestination = typeMap.TypeConverter(source, destination, context.OperationContext);
                }
                catch (MappingException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MappingException(
                        $"Converting '{typeMap.SourceType.FullName}' to " +
                        $"'{typeMap.DestinationType.FullName}' failed.",
                        exception);
                }

                if (convertedDestination is not null)
                {
                    RunAfterMapActions(typeMap, source, convertedDestination, context.OperationContext);
                }

                return convertedDestination;
            }

            if (destination is null)
            {
                try
                {
                    destination = typeMap.DestinationConstructor is null
                        ? CreateDestination(destinationType)
                        : typeMap.DestinationConstructor(source, context.OperationContext);
                }
                catch (MappingException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new MappingException(
                        $"Constructing destination '{typeMap.DestinationType.FullName}' failed.",
                        exception);
                }

                if (destination is null)
                {
                    throw new MappingException(
                        $"Constructing destination '{typeMap.DestinationType.FullName}' returned null.");
                }
            }

            foreach (var memberMap in typeMap.MemberMaps)
            {
                object? sourceValue;
                object? destinationValue;

                try
                {
                    destinationValue = memberMap.DestinationProperty.GetValue(destination);
                    sourceValue = memberMap.SourceResolver(
                        source,
                        destination,
                        destinationValue,
                        context.OperationContext);
                }
                catch (Exception exception)
                {
                    throw new MappingException(
                        $"Reading member '{memberMap.DestinationProperty.Name}' while mapping " +
                        $"'{typeMap.SourceType.FullName}' to '{typeMap.DestinationType.FullName}' failed.",
                        exception);
                }

                object? mappedValue;
                try
                {
                    mappedValue = MapMemberValue(
                        sourceValue,
                        memberMap.SourceValueType,
                        memberMap.DestinationProperty.PropertyType,
                        destinationValue,
                        context);
                }
                catch (Exception exception) when (exception is not MappingException)
                {
                    throw new MappingException(
                        $"Mapping member '{memberMap.DestinationProperty.Name}' while mapping " +
                        $"'{typeMap.SourceType.FullName}' to '{typeMap.DestinationType.FullName}' failed.",
                        exception);
                }

                if (memberMap.Condition is not null &&
                    !memberMap.Condition(source, destination, mappedValue, destinationValue))
                {
                    continue;
                }

                if (memberMap.DestinationProperty.SetMethod?.IsPublic != true)
                {
                    continue;
                }

                try
                {
                    memberMap.DestinationProperty.SetValue(destination, mappedValue);
                }
                catch (Exception exception)
                {
                    throw new MappingException(
                        $"Writing member '{memberMap.DestinationProperty.Name}' while mapping " +
                        $"'{typeMap.SourceType.FullName}' to '{typeMap.DestinationType.FullName}' failed.",
                        exception);
                }
            }

            RunAfterMapActions(typeMap, source, destination, context.OperationContext);

            return destination;
        }
        catch (MappingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MappingException(
                $"Mapping '{typeMap.SourceType.FullName}' to '{typeMap.DestinationType.FullName}' failed.",
                exception);
        }
        finally
        {
            context.Exit(typePair);
        }
    }

    private static void RunAfterMapActions(
        TypeMapDefinition typeMap,
        object source,
        object destination,
        MappingOperationContext context)
    {
        foreach (var action in typeMap.AfterMapActions)
        {
            try
            {
                action(source, destination, context);
            }
            catch (MappingException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new MappingException(
                    $"AfterMap action failed while mapping '{typeMap.SourceType.FullName}' to " +
                    $"'{typeMap.DestinationType.FullName}'.",
                    exception);
            }
        }
    }

    private object? MapMemberValue(
        object? sourceValue,
        Type declaredSourceType,
        Type destinationType,
        object? destinationValue,
        MappingContext context)
    {
        if (sourceValue is null)
        {
            if (TryGetDictionaryTypes(destinationType, out var destinationKeyType, out var destinationValueType))
            {
                return MapDictionary(
                    Array.Empty<object>(),
                    declaredSourceType,
                    destinationType,
                    destinationKeyType,
                    destinationValueType,
                    destinationValue,
                    context);
            }

            if (TryGetCollectionElementType(destinationType, out var destinationElementType))
            {
                return MapCollection(
                    Array.Empty<object>(),
                    declaredSourceType,
                    destinationType,
                    destinationElementType,
                    destinationValue,
                    context);
            }

            if (IsNonGenericListDestination(destinationType))
            {
                return MapNonGenericList(
                    Array.Empty<object>(),
                    declaredSourceType,
                    destinationType,
                    destinationValue);
            }

            return CreateNullValue(destinationType);
        }

        return MapCore(sourceValue, declaredSourceType, destinationType, destinationValue, context);
    }

    private object? ConvertWithoutTypeMap(object source, Type destinationType, MappingContext context)
    {
        var sourceType = source.GetType();

        if (destinationType.IsAssignableFrom(sourceType))
        {
            return source;
        }

        var nullableDestinationType = Nullable.GetUnderlyingType(destinationType);
        if (nullableDestinationType is not null)
        {
            return ConvertWithoutTypeMap(source, nullableDestinationType, context);
        }

        if (destinationType.IsEnum)
        {
            if (source is string enumName)
            {
                return Enum.Parse(destinationType, enumName, ignoreCase: true);
            }

            return Enum.ToObject(destinationType, source);
        }

        if (source is IConvertible && typeof(IConvertible).IsAssignableFrom(destinationType))
        {
            return Convert.ChangeType(source, destinationType, CultureInfo.InvariantCulture);
        }

        throw new MappingException(
            $"No map is configured from '{sourceType.FullName}' to '{destinationType.FullName}'.");
    }

    private object MapDictionary(
        IEnumerable source,
        Type declaredSourceType,
        Type destinationType,
        Type destinationKeyType,
        Type destinationValueType,
        object? destination,
        MappingContext context)
    {
        var isReadOnlyDestination = IsReadOnlyDictionaryType(destinationType);
        var keyValuePairType = typeof(KeyValuePair<,>).MakeGenericType(destinationKeyType, destinationValueType);
        var collectionInterface = typeof(ICollection<>).MakeGenericType(keyValuePairType);

        if (isReadOnlyDestination ||
            destination is not null &&
            (!collectionInterface.IsInstanceOfType(destination) ||
             (bool)collectionInterface.GetProperty(nameof(ICollection<object>.IsReadOnly))!
                 .GetValue(destination)!))
        {
            destination = null;
        }

        var dictionary = destination ?? CreateDictionary(destinationType, destinationKeyType, destinationValueType);
        if (!collectionInterface.IsInstanceOfType(dictionary))
        {
            throw new MappingException(
                $"Destination dictionary type '{destinationType.FullName}' does not implement " +
                $"ICollection<{keyValuePairType.Name}>.");
        }

        collectionInterface.GetMethod(nameof(ICollection<object>.Clear))!.Invoke(dictionary, null);
        var addMethod = collectionInterface.GetMethod(nameof(ICollection<object>.Add))!;
        var keyValuePairConstructor = keyValuePairType.GetConstructor(
            [destinationKeyType, destinationValueType])!;
        var hasDeclaredSourceTypes = TryGetDictionaryItemTypes(
            declaredSourceType,
            out var declaredSourceKeyType,
            out var declaredSourceValueType);
        var hasRuntimeSourceTypes = TryGetDictionaryItemTypes(
            source.GetType(),
            out var runtimeSourceKeyType,
            out var runtimeSourceValueType);
        var sourceItems = hasRuntimeSourceTypes
            ? EnumerateGenericItems(
                source,
                typeof(KeyValuePair<,>).MakeGenericType(runtimeSourceKeyType, runtimeSourceValueType))
            : source.Cast<object?>();

        foreach (var sourceItem in sourceItems)
        {
            if (sourceItem is null)
            {
                continue;
            }

            var sourceItemType = sourceItem.GetType();
            var keyProperty = sourceItemType.GetProperty("Key", BindingFlags.Instance | BindingFlags.Public);
            var valueProperty = sourceItemType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            if (keyProperty is null || valueProperty is null)
            {
                throw new MappingException(
                    $"Source dictionary item type '{sourceItemType.FullName}' does not expose Key and Value properties.");
            }

            var sourceKey = keyProperty.GetValue(sourceItem);
            var sourceValue = valueProperty.GetValue(sourceItem);
            var mappedKey = MapCore(
                sourceKey,
                hasDeclaredSourceTypes
                    ? declaredSourceKeyType
                    : hasRuntimeSourceTypes
                        ? runtimeSourceKeyType
                        : keyProperty.PropertyType,
                destinationKeyType,
                destination: null,
                context);
            var mappedValue = MapCore(
                sourceValue,
                hasDeclaredSourceTypes
                    ? declaredSourceValueType
                    : hasRuntimeSourceTypes
                        ? runtimeSourceValueType
                        : valueProperty.PropertyType,
                destinationValueType,
                destination: null,
                context);
            var mappedPair = keyValuePairConstructor.Invoke([mappedKey, mappedValue]);
            addMethod.Invoke(dictionary, [mappedPair]);
        }

        if (!isReadOnlyDestination)
        {
            return dictionary;
        }

        var readOnlyDictionaryType = typeof(ReadOnlyDictionary<,>)
            .MakeGenericType(destinationKeyType, destinationValueType);
        return Activator.CreateInstance(readOnlyDictionaryType, dictionary)!;
    }

    private object MapKeyValuePair(
        object source,
        Type sourceKeyType,
        Type sourceValueType,
        Type destinationType,
        Type destinationKeyType,
        Type destinationValueType,
        MappingContext context)
    {
        var sourceType = source.GetType();
        var sourceKey = sourceType.GetProperty("Key")!.GetValue(source);
        var sourceValue = sourceType.GetProperty("Value")!.GetValue(source);
        var mappedKey = MapCore(
            sourceKey,
            sourceKeyType,
            destinationKeyType,
            destination: null,
            context);
        var mappedValue = MapCore(
            sourceValue,
            sourceValueType,
            destinationValueType,
            destination: null,
            context);
        var constructor = destinationType.GetConstructor([destinationKeyType, destinationValueType])!;
        return constructor.Invoke([mappedKey, mappedValue]);
    }

    private static IEnumerable<object?> EnumerateGenericItems(IEnumerable source, Type itemType)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(itemType);
        if (!enumerableType.IsInstanceOfType(source))
        {
            foreach (var sourceItem in source)
            {
                yield return sourceItem;
            }

            yield break;
        }

        var getEnumeratorMethod = enumerableType.GetMethod(nameof(IEnumerable.GetEnumerator))!;
        var enumerator = (IEnumerator)getEnumeratorMethod.Invoke(source, null)!;
        try
        {
            while (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static object CreateDictionary(Type destinationType, Type keyType, Type valueType)
    {
        if (IsReadOnlyDictionaryType(destinationType) || destinationType.IsInterface || destinationType.IsAbstract)
        {
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;
        }

        return CreateDestination(destinationType);
    }

    private static bool IsReadOnlyDictionaryType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var typeDefinition = type.GetGenericTypeDefinition();
        return typeDefinition == typeof(IReadOnlyDictionary<,>) ||
               typeDefinition == typeof(ReadOnlyDictionary<,>);
    }

    private static bool IsSupportedCollectionDestination(Type type)
    {
        if (type.IsArray || type.IsInterface || typeof(IList).IsAssignableFrom(type))
        {
            return true;
        }

        return type
            .GetInterfaces()
            .Append(type)
            .Any(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(ICollection<>));
    }

    private static bool IsNonGenericListDestination(Type type)
        => type == typeof(IEnumerable) ||
           type == typeof(ICollection) ||
           type == typeof(IList) ||
           typeof(IList).IsAssignableFrom(type);

    private static object MapNonGenericList(
        IEnumerable source,
        Type declaredSourceType,
        Type destinationType,
        object? destination)
    {
        if (destination is IList existingList &&
            (existingList.IsReadOnly || existingList.IsFixedSize))
        {
            destination = null;
        }

        var list = destination as IList ?? CreateNonGenericList(destinationType);
        if (list.IsReadOnly || list.IsFixedSize)
        {
            throw new MappingException(
                $"Destination collection type '{destinationType.FullName}' does not expose a mutable IList.");
        }

        list.Clear();
        var sourceElementType = TryGetCollectionElementType(declaredSourceType, out var declaredElementType)
            ? declaredElementType
            : TryGetCollectionElementType(source.GetType(), out var runtimeElementType)
                ? runtimeElementType
                : typeof(object);
        foreach (var sourceItem in EnumerateGenericItems(source, sourceElementType))
        {
            list.Add(sourceItem);
        }

        return list;
    }

    private static IList CreateNonGenericList(Type destinationType)
    {
        if (destinationType.IsInterface || destinationType.IsAbstract)
        {
            return new List<object?>();
        }

        return CreateDestination(destinationType) as IList
            ?? throw new MappingException(
                $"Destination collection type '{destinationType.FullName}' does not implement IList.");
    }

    private object MapCollection(
        IEnumerable source,
        Type declaredSourceType,
        Type destinationType,
        Type destinationElementType,
        object? destination,
        MappingContext context)
    {
        var isReadOnlyDestination = IsReadOnlyCollectionType(destinationType);
        var sourceElementType = TryGetCollectionElementType(declaredSourceType, out var declaredElementType)
            ? declaredElementType
            : TryGetCollectionElementType(source.GetType(), out var runtimeElementType)
                ? runtimeElementType
                : typeof(object);
        var sourceItems = EnumerateGenericItems(source, sourceElementType).ToList();

        if (destinationType.IsArray)
        {
            var array = Array.CreateInstance(destinationElementType, sourceItems.Count);
            for (var index = 0; index < sourceItems.Count; index++)
            {
                array.SetValue(
                    MapElement(sourceItems[index], sourceElementType, destinationElementType, context),
                    index);
            }

            return array;
        }

        var collectionInterface = typeof(ICollection<>).MakeGenericType(destinationElementType);
        if (isReadOnlyDestination ||
            destination is not null &&
            (!collectionInterface.IsInstanceOfType(destination) ||
             (bool)collectionInterface.GetProperty(nameof(ICollection<object>.IsReadOnly))!
                 .GetValue(destination)!))
        {
            destination = null;
        }

        var collection = destination ?? CreateCollection(destinationType, destinationElementType);
        if (!collectionInterface.IsInstanceOfType(collection))
        {
            throw new MappingException(
                $"Destination collection type '{destinationType.FullName}' does not implement " +
                $"ICollection<{destinationElementType.Name}>.");
        }

        collectionInterface.GetMethod(nameof(ICollection<object>.Clear))!.Invoke(collection, null);
        var addMethod = collectionInterface.GetMethod(nameof(ICollection<object>.Add))!;

        foreach (var sourceItem in sourceItems)
        {
            addMethod.Invoke(
                collection,
                [MapElement(sourceItem, sourceElementType, destinationElementType, context)]);
        }

        if (!isReadOnlyDestination)
        {
            return collection;
        }

        return Activator.CreateInstance(destinationType, collection)!;
    }

    private object? MapElement(
        object? sourceItem,
        Type declaredSourceElementType,
        Type destinationElementType,
        MappingContext context)
    {
        if (sourceItem is null)
        {
            return CreateNullValue(destinationElementType);
        }

        return MapCore(
            sourceItem,
            declaredSourceElementType,
            destinationElementType,
            destination: null,
            context);
    }

    private static object CreateCollection(Type destinationType, Type elementType)
    {
        if (!destinationType.IsInterface &&
            !destinationType.IsAbstract &&
            !IsReadOnlyCollectionType(destinationType))
        {
            return CreateDestination(destinationType);
        }

        if (destinationType.IsGenericType &&
            (destinationType.GetGenericTypeDefinition() == typeof(ISet<>) ||
             destinationType.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)))
        {
            return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(elementType))!;
        }

        return Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
    }

    private static bool IsReadOnlyCollectionType(Type type)
        => type.IsGenericType &&
           type.GetGenericTypeDefinition() == typeof(ReadOnlyCollection<>);

    private static object CreateDestination(Type destinationType)
    {
        try
        {
            return Activator.CreateInstance(destinationType, nonPublic: true)
                ?? throw new MappingException(
                    $"Destination type '{destinationType.FullName}' could not be constructed.");
        }
        catch (MappingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MappingException(
                $"Destination type '{destinationType.FullName}' requires a parameterless constructor.",
                exception);
        }
    }

    private static object? CreateNullValue(Type destinationType)
    {
        if (!destinationType.IsValueType || Nullable.GetUnderlyingType(destinationType) is not null)
        {
            return null;
        }

        return Activator.CreateInstance(destinationType);
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

    private static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        var dictionaryType = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dictionaryType is null)
        {
            keyType = null!;
            valueType = null!;
            return false;
        }

        var arguments = dictionaryType.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool TryGetDictionaryItemTypes(Type type, out Type keyType, out Type valueType)
    {
        if (TryGetDictionaryTypes(type, out keyType, out valueType))
        {
            return true;
        }

        var enumerableType = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                candidate.GetGenericArguments()[0].IsGenericType &&
                candidate.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>));
        if (enumerableType is null)
        {
            keyType = typeof(object);
            valueType = typeof(object);
            return false;
        }

        var arguments = enumerableType.GetGenericArguments()[0].GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool TryGetKeyValuePairTypes(Type type, out Type keyType, out Type valueType)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
        {
            keyType = typeof(object);
            valueType = typeof(object);
            return false;
        }

        var arguments = type.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private sealed class MappingContext
    {
        private readonly Dictionary<TypePair, int> _depths = [];

        public MappingContext(MappingOperationContext operationContext)
        {
            OperationContext = operationContext;
        }

        public MappingOperationContext OperationContext { get; }

        public bool TryEnter(TypePair typePair, int maximumDepth)
        {
            _depths.TryGetValue(typePair, out var currentDepth);
            var nextDepth = currentDepth + 1;
            if (nextDepth > maximumDepth)
            {
                return false;
            }

            _depths[typePair] = nextDepth;
            return true;
        }

        public void Exit(TypePair typePair)
        {
            var currentDepth = _depths[typePair];
            if (currentDepth == 1)
            {
                _depths.Remove(typePair);
                return;
            }

            _depths[typePair] = currentDepth - 1;
        }
    }

    private sealed class ContextualMapper : IMapper
    {
        private readonly Mapper _mapper;
        private MappingContext? _context;

        private MappingContext Context => _context
            ?? throw new InvalidOperationException("The mapping context has not been attached.");

        internal ContextualMapper(Mapper mapper)
        {
            _mapper = mapper;
        }

        internal void Attach(MappingContext context)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("The mapping context is already attached.");
            }

            _context = context;
        }

        IConfigurationProvider IMapper.ConfigurationProvider => _mapper.ConfigurationProvider;

        TDestination IMapper.Map<TDestination>(object? source)
            => (TDestination)_mapper.MapCore(
                source,
                source?.GetType() ?? typeof(object),
                typeof(TDestination),
                destination: null,
                Context)!;

        TDestination IMapper.Map<TSource, TDestination>(TSource source)
            => (TDestination)_mapper.MapCore(
                source,
                typeof(TSource),
                typeof(TDestination),
                destination: null,
                Context)!;

        TDestination IMapper.Map<TSource, TDestination>(TSource source, TDestination destination)
            => (TDestination)_mapper.MapCore(
                source,
                typeof(TSource),
                typeof(TDestination),
                destination,
                Context)!;

        object? IMapper.Map(object? source, Type sourceType, Type destinationType)
            => _mapper.MapCore(
                source,
                sourceType,
                destinationType,
                destination: null,
                Context);

        object? IMapper.Map(object? source, object? destination, Type sourceType, Type destinationType)
            => _mapper.MapCore(
                source,
                sourceType,
                destinationType,
                destination,
                Context);
    }
}
