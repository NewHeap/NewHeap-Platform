using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <summary>
/// The subject type of projects for agent threads.
/// </summary>
public static class ProjectSubjects
{
    public const string Type = "project";

    /// <summary>
    /// SPM-301: where the management portal shows one project's agent work: the intake reviews
    /// page with the project chosen.
    /// </summary>
    public const string LinkTemplate = "/management/intake-reviews?project={id}";

    public static NhAiSubjectRef For(Guid projectId)
    {
        return new NhAiSubjectRef(Type, projectId.ToString("D"));
    }
}

/// <summary>
/// Tells the assistant who may see and act on the agent threads of a project. Project members
/// keep their role; the project owner is an approver without a member row. The assistant asks on
/// every request, so removing a member takes effect at once. SPM-301: the same class finds
/// projects by key or name for people who choose a project, among the projects they own or are a
/// member of; the assistant still checks every result with <see cref="ResolveAsync"/>.
/// </summary>
public sealed class ProjectSubjectAccessResolver(SampleProjectManagementDbContext dbContext)
    : INhAiSubjectAccessResolver, INhAiSubjectSearch
{
    public string SubjectType => ProjectSubjects.Type;

    public async IAsyncEnumerable<NhAiSubjectInfo> SearchAsync(
        NhAiSubjectSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(request.ActorId, out var userId))
        {
            yield break;
        }

        var projects = await Find(dbContext.Projects.AsNoTracking(), userId, request.Query, request.Limit)
            .Select(project => new
            {
                project.Id,
                project.DivisionId,
                project.Key,
                project.Name,
                project.OwnerUserId,
                project.LastModifiedDateTime
            })
            .ToListAsync(cancellationToken);
        foreach (var project in projects)
        {
            yield return Describe(
                ProjectSubjects.For(project.Id),
                project.DivisionId,
                project.Key,
                project.Name,
                project.OwnerUserId,
                project.LastModifiedDateTime);
        }
    }

    public async ValueTask<NhAiSubjectInfo?> DescribeAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetProjectId(subject, out var projectId))
        {
            return null;
        }

        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(item => item.Id == projectId)
            .Select(item => new
            {
                item.DivisionId,
                item.Key,
                item.Name,
                item.OwnerUserId,
                item.LastModifiedDateTime
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return null;
        }

        return Describe(subject, project.DivisionId, project.Key, project.Name, project.OwnerUserId, project.LastModifiedDateTime);
    }

    /// <summary>
    /// The projects the search finds: those the person owns or is a member of whose key or name
    /// contains the query in any case, by key, at most <paramref name="limit"/>. Provider-neutral:
    /// SQL Server and PostgreSQL both translate <c>ToLower</c> and <c>Contains</c>, so the query
    /// stays server-side and finds the same projects on both.
    /// </summary>
    public static IQueryable<Project> Find(IQueryable<Project> projects, Guid userId, string query, int limit)
    {
        var text = query.ToLowerInvariant();
        return projects
            .Where(project => project.OwnerUserId == userId
                || project.Members.Any(member => member.UserId == userId))
            .Where(project => project.Key.ToLower().Contains(text) || project.Name.ToLower().Contains(text))
            .OrderBy(project => project.Key)
            .ThenBy(project => project.Id)
            .Take(limit);
    }

    private static NhAiSubjectInfo Describe(
        NhAiSubjectRef subject,
        Guid divisionId,
        string key,
        string name,
        Guid? ownerUserId,
        DateTimeOffset lastModified)
    {
        return new NhAiSubjectInfo(subject, NhAiSubjectInfo.CreateScopeKey(null, divisionId))
        {
            DivisionId = divisionId,
            // Any project edit changes the version, so an approved proposal for an edited project
            // closes as stale instead of executing.
            Version = lastModified.UtcTicks.ToString(CultureInfo.InvariantCulture),
            DisplayName = key + " — " + name,
            ResponsibleActorId = ownerUserId?.ToString("D")
        };
    }

    public async ValueTask<NhAiSubjectAccess> ResolveAsync(
        NhAiSubjectRef subject,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetProjectId(subject, out var projectId) || !Guid.TryParse(actorId, out var userId))
        {
            return NhAiSubjectAccess.None;
        }

        var isOwner = await dbContext.Projects
            .AsNoTracking()
            .AnyAsync(project => project.Id == projectId && project.OwnerUserId == userId, cancellationToken);
        if (isOwner)
        {
            return new NhAiSubjectAccess(NhAiSubjectRole.Approver);
        }

        var role = await dbContext.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.UserId == userId)
            .Select(member => (ProjectMemberRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);
        return role is { } memberRole ? new NhAiSubjectAccess(ToSubjectRole(memberRole)) : NhAiSubjectAccess.None;
    }

    public async IAsyncEnumerable<NhAiSubjectMember> ListMembersAsync(
        NhAiSubjectRef subject,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!TryGetProjectId(subject, out var projectId))
        {
            yield break;
        }

        var owner = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId && project.OwnerUserId != null)
            .Select(project => new { UserId = project.OwnerUserId!.Value, project.OwnerUser!.UserName })
            .SingleOrDefaultAsync(cancellationToken);
        if (owner is not null)
        {
            yield return new NhAiSubjectMember(owner.UserId.ToString("D"), NhAiSubjectRole.Approver)
            {
                DisplayName = owner.UserName
            };
        }

        var members = await dbContext.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId)
            .OrderBy(member => member.UserId)
            .Select(member => new { member.UserId, member.Role, member.User.UserName })
            .ToListAsync(cancellationToken);
        foreach (var member in members.Where(member => member.UserId != owner?.UserId))
        {
            yield return new NhAiSubjectMember(member.UserId.ToString("D"), ToSubjectRole(member.Role))
            {
                DisplayName = member.UserName
            };
        }
    }

    private static NhAiSubjectRole ToSubjectRole(ProjectMemberRole role)
    {
        return role switch
        {
            ProjectMemberRole.Approver => NhAiSubjectRole.Approver,
            ProjectMemberRole.Participant => NhAiSubjectRole.Participant,
            _ => NhAiSubjectRole.Reader
        };
    }

    /// <summary>
    /// Accepts only the canonical id that <see cref="ProjectSubjects.For"/> writes. A model that
    /// consults an agent may type the id differently; another spelling must not open a second
    /// thread about the same project.
    /// </summary>
    private static bool TryGetProjectId(NhAiSubjectRef subject, out Guid projectId)
    {
        projectId = Guid.Empty;
        return string.Equals(subject.Type, ProjectSubjects.Type, StringComparison.Ordinal)
               && Guid.TryParseExact(subject.Id, "D", out projectId)
               && string.Equals(subject.Id, projectId.ToString("D"), StringComparison.Ordinal);
    }
}
