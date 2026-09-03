using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Mapping;
using NSubstitute;
using System.Collections;
using System.ComponentModel;
using System.Data.Common;
using System.Linq.Expressions;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class CollectionProcessingDeadlockRetryTests
{
    [Fact]
    public async Task SqlServerDeadlockUsesTheDefaultFiveAttempts()
    {
        var settings = new NewHeapCommonSettings();
        var plan = new RetryPlan(
            countFailures: 4,
            enumerationFailures: 0,
            () => new TestDatabaseException(number: 1205));
        var queryable = new RetryQueryable<TestEntity>(
            [new TestEntity { Id = 1 }],
            plan);
        var service = CreateService(settings);

        var result = await service.ProcessQueryable<TestEntity, TestEntity>(
            CreateRequest(),
            queryable,
            cancellationToken: default,
            (entity => (object)entity.Id, ListSortDirection.Ascending));

        Assert.Equal(5, settings.CollectionProcessingDeadlockMaxAttempts);
        Assert.Equal(1, result.totalCount);
        Assert.Equal(5, plan.CountAttempts);
    }

    [Fact]
    public async Task ConfiguredMaximumStopsDeadlockRetriesAtTheBound()
    {
        var settings = new NewHeapCommonSettings
        {
            CollectionProcessingDeadlockMaxAttempts = 2
        };
        var plan = new RetryPlan(
            countFailures: 2,
            enumerationFailures: 0,
            () => new TestDatabaseException(number: 1205));
        var queryable = new RetryQueryable<TestEntity>(
            [new TestEntity { Id = 1 }],
            plan);
        var service = CreateService(settings);

        await Assert.ThrowsAsync<TestDatabaseException>(() =>
            service.ProcessQueryable<TestEntity, TestEntity>(
                CreateRequest(),
                queryable,
                cancellationToken: default,
                (entity => (object)entity.Id, ListSortDirection.Ascending)));

        Assert.Equal(2, plan.CountAttempts);
    }

    [Fact]
    public async Task PostgreSqlDeadlockDuringItemMaterializationIsRetried()
    {
        var plan = new RetryPlan(
            countFailures: 0,
            enumerationFailures: 1,
            () => new DbUpdateException(
                "The provider command failed.",
                new TestDatabaseException(sqlState: "40P01")));
        var queryable = new RetryQueryable<TestEntity>(
            [new TestEntity { Id = 1 }],
            plan);
        var service = CreateService(new NewHeapCommonSettings());

        var result = await service.GetCollectionResultModelAsync<TestEntity, TestEntity>(
            CreateRequest(),
            queryable,
            resultQueryableFunc: null,
            asNoTracking: false,
            cancellationToken: default,
            (entity => (object)entity.Id, ListSortDirection.Ascending));

        Assert.Single(result.Items);
        Assert.Equal(1, plan.CountAttempts);
        Assert.Equal(2, plan.EnumerationAttempts);
    }

    [Fact]
    public async Task NonDeadlockDatabaseFailureIsNotRetried()
    {
        var plan = new RetryPlan(
            countFailures: 1,
            enumerationFailures: 0,
            () => new TestDatabaseException(number: 999));
        var queryable = new RetryQueryable<TestEntity>(
            [new TestEntity { Id = 1 }],
            plan);
        var service = CreateService(new NewHeapCommonSettings());

        await Assert.ThrowsAsync<TestDatabaseException>(() =>
            service.ProcessQueryable<TestEntity, TestEntity>(
                CreateRequest(),
                queryable,
                cancellationToken: default,
                (entity => (object)entity.Id, ListSortDirection.Ascending)));

        Assert.Equal(1, plan.CountAttempts);
    }

    [Fact]
    public void InvalidAttemptMaximumIsRejected()
    {
        var settings = new NewHeapCommonSettings
        {
            CollectionProcessingDeadlockMaxAttempts = 0
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateService(settings));
    }

    private static CollectionProcessingService CreateService(NewHeapCommonSettings settings)
    {
        return new CollectionProcessingService(
            Substitute.For<IMapper>(),
            Options.Create(settings));
    }

    private static CollectionRequestModel CreateRequest()
    {
        return new CollectionRequestModel
        {
            Page = 1,
            ItemsPerPage = 20
        };
    }

    private sealed class TestEntity
    {
        public int Id { get; set; }
    }

    private sealed class TestDatabaseException : DbException
    {
        public TestDatabaseException(int number = 0, string? sqlState = null)
            : base("Test database failure.")
        {
            Number = number;
            SqlState = sqlState;
        }

        public int Number { get; }

        public override string? SqlState { get; }
    }

    private sealed class RetryPlan
    {
        private readonly int _countFailures;
        private readonly int _enumerationFailures;
        private readonly Func<Exception> _exceptionFactory;

        public RetryPlan(
            int countFailures,
            int enumerationFailures,
            Func<Exception> exceptionFactory)
        {
            _countFailures = countFailures;
            _enumerationFailures = enumerationFailures;
            _exceptionFactory = exceptionFactory;
        }

        public int CountAttempts { get; private set; }
        public int EnumerationAttempts { get; private set; }

        public void BeforeCount()
        {
            CountAttempts++;

            if (CountAttempts <= _countFailures)
            {
                throw _exceptionFactory();
            }
        }

        public void BeforeEnumeration()
        {
            EnumerationAttempts++;

            if (EnumerationAttempts <= _enumerationFailures)
            {
                throw _exceptionFactory();
            }
        }
    }

    private sealed class RetryQueryable<T> : IOrderedQueryable<T>
    {
        private readonly IQueryable<T> _inner;
        private readonly RetryPlan _plan;

        public RetryQueryable(IEnumerable<T> source, RetryPlan plan)
            : this(source.AsQueryable(), plan)
        {
        }

        public RetryQueryable(IQueryable<T> inner, RetryPlan plan)
        {
            _inner = inner;
            _plan = plan;
            Provider = new RetryQueryProvider(inner.Provider, plan);
        }

        public Type ElementType => typeof(T);
        public Expression Expression => _inner.Expression;
        public IQueryProvider Provider { get; }

        public IEnumerator<T> GetEnumerator()
        {
            _plan.BeforeEnumeration();
            return _inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    private sealed class RetryQueryProvider : IQueryProvider
    {
        private readonly IQueryProvider _inner;
        private readonly RetryPlan _plan;

        public RetryQueryProvider(IQueryProvider inner, RetryPlan plan)
        {
            _inner = inner;
            _plan = plan;
        }

        public IQueryable CreateQuery(Expression expression)
        {
            var innerQuery = _inner.CreateQuery(expression);
            var queryType = typeof(RetryQueryable<>).MakeGenericType(innerQuery.ElementType);

            return (IQueryable)Activator.CreateInstance(queryType, innerQuery, _plan)!;
        }

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            return new RetryQueryable<TElement>(
                _inner.CreateQuery<TElement>(expression),
                _plan);
        }

        public object? Execute(Expression expression)
        {
            _plan.BeforeCount();
            return _inner.Execute(expression);
        }

        public TResult Execute<TResult>(Expression expression)
        {
            _plan.BeforeCount();
            return _inner.Execute<TResult>(expression);
        }
    }
}
