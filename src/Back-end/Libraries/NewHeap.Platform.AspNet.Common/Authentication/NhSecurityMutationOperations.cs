using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Runs security-sensitive account mutations in one transaction and invalidates every
/// authentication session of the user after the mutation succeeds.
/// </summary>
internal static class NhSecurityMutationOperations
{
    internal static async Task<TaskResult> ExecuteWithSessionInvalidationAsync<TUser>(
        UserManager<TUser> userManager,
        IRepository<TUser> userRepository,
        INhDbLogService dbLogService,
        TUser user,
        Func<Task<TaskResult>> mutation,
        string logMessage,
        Guid? committedByUserId,
        string logTag,
        CancellationToken cancellationToken)
        where TUser : IdentityUser<Guid>
    {
        await using var transaction = await userRepository.StartOrGetTransactionScopeAsync(cancellationToken);

        try
        {
            var mutationResult = await mutation();
            if (!mutationResult.Success)
            {
                await transaction.RollbackAsync(cancellationToken);
                return mutationResult;
            }

            var securityStamp = await userManager.GetSecurityStampAsync(user);
            if (string.IsNullOrEmpty(securityStamp))
            {
                throw new InvalidOperationException("The user does not have a security stamp.");
            }

            var markerResult = await userManager.SetAuthenticationTokenAsync(
                user,
                NhAuthenticationSessionDefaults.LoginProvider,
                NhAuthenticationSessionDefaults.SecurityStampTokenName,
                securityStamp);

            if (!markerResult.Succeeded)
            {
                throw new InvalidOperationException("Could not record the authentication session state.");
            }

            await NhRefreshTokenOperations.RevokeAllAsync(
                userRepository.GetDbSet<NhUserAuthRefreshToken>(),
                user.Id,
                cancellationToken);

            await dbLogService.LogAsync(
                message: logMessage,
                messageArguments: new[] { user.Id.ToString() },
                objectId: user.Id.ToString(),
                objectType: typeof(TUser).Name,
                objectTypeFull: typeof(TUser).FullName,
                userId: committedByUserId,
                action: LogAction.Update,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: logTag);

            await transaction.CommitAsync(cancellationToken);
            return mutationResult;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (transaction.IsMyTransaction)
            {
                userRepository.ClearTracking();
            }

            throw;
        }
    }
}
