using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-203–207: small server-side NewHeap helpers. These tests deliberately
/// use the concrete public APIs so their failure and cancellation boundaries
/// remain documented alongside the larger application-service samples.
/// </summary>
public sealed class ServerHelperExtensionSamplesTests
{
    [Fact]
    public async Task SemaphoreWrappersSerializeConcurrentCriticalSections()
    {
        var singlePermit = new SemaphoreLocker();
        var twoPermits = new SemaphoreSlimAsync(initialCount: 2, maxCount: 2);

        var singlePermitMaximum = await GetMaximumConcurrencyAsync(singlePermit.LockAsync, 5);
        var twoPermitMaximum = await GetMaximumConcurrencyAsync(twoPermits.LockAsync, 6);

        Assert.Equal(1, singlePermitMaximum);
        Assert.Equal(2, twoPermitMaximum);
    }

    [Fact]
    public async Task SemaphoreWrapperReleasesItsPermitWhenWorkFails()
    {
        var gate = new SemaphoreLocker();
        var completedAfterFailure = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.LockAsync(() =>
            throw new InvalidOperationException("Intentional critical-section failure")));

        await gate.LockAsync(() =>
        {
            completedAfterFailure = true;
            return Task.CompletedTask;
        });

        Assert.True(completedAfterFailure);
    }

    [Fact]
    public void SafeFormattableStringKeepsLoggingSafeWhenValuesOrFormatsAreBroken()
    {
        var throwingValue = SafeFormattableStringFactory.Create(
            "Project {0}",
            new ThrowingToString());
        var malformedFormat = SafeFormattableStringFactory.Create("Project {not-an-index}", "NHP");

        Assert.Equal("Project [[ToString() failed: InvalidOperationException]]", throwingValue.ToString());
        Assert.Equal("Project {not-an-index}", malformedFormat.ToString());
    }

    [Fact]
    public void IdentityErrorsArePreservedAsTaskResultItems()
    {
        var identityResult = IdentityResult.Failed(new IdentityError
        {
            Code = "DuplicateProjectKey",
            Description = "A project with this key already exists."
        });
        var result = identityResult.ToTaskResult(new TaskResult());

        Assert.False(result.Success);
        Assert.Contains(result.AllErrorMessages, error =>
            error.ToString().Contains("DuplicateProjectKey", StringComparison.Ordinal));
    }

    [Fact]
    public void JwtValidationOptionsReadIssuerAudienceKeyAndZeroClockSkewFromConfiguration()
    {
        const string issuer = "sample-project-management";
        const string signingKey = "sample-project-management-signing-key-2026";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = issuer,
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = signingKey
            })
            .Build();
        var parameters = new TokenValidationParameters();

        parameters.ConfigureNhJwtBearerValidationOptions(configuration);

        var signingKeyBytes = Assert.IsType<SymmetricSecurityKey>(parameters.IssuerSigningKey).Key;
        Assert.Equal(issuer, parameters.ValidIssuer);
        Assert.Equal(issuer, parameters.ValidAudience);
        Assert.Equal(Encoding.UTF8.GetBytes(signingKey), signingKeyBytes);
        Assert.True(parameters.ValidateLifetime);
        Assert.Equal(TimeSpan.Zero, parameters.ClockSkew);
    }

    private static async Task<int> GetMaximumConcurrencyAsync(
        Func<Func<Task>, Task> lockAsync,
        int workItemCount)
    {
        var active = 0;
        var maximum = 0;
        var completed = new ConcurrentQueue<int>();

        await Task.WhenAll(Enumerable.Range(1, workItemCount).Select(item => lockAsync(async () =>
        {
            var nowActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, nowActive);
            await Task.Delay(10);
            completed.Enqueue(item);
            Interlocked.Decrement(ref active);
        })));

        Assert.Equal(workItemCount, completed.Count);
        return maximum;
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (current >= value || Interlocked.CompareExchange(ref maximum, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("Sample formatting failure");
    }
}
