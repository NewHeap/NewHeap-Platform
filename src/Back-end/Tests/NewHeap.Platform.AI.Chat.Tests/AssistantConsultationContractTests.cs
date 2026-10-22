using System.Text.Json;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The JSON contracts that consultation proposals add to: every field is additive and optional, so
/// clients of version 1 keep working (CP19).
/// </summary>
public sealed class AssistantConsultationContractTests
{
    [Fact]
    public void An_approval_part_names_the_consultation_the_asker_decides_and_omits_it_otherwise()
    {
        var consultationId = Guid.NewGuid();
        var part = new NhAssistantApprovalPartDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "hash",
            "projects.change-status",
            "Approval is required before this tool can run.",
            null,
            "{}",
            [],
            DateTimeOffset.UnixEpoch,
            "pending")
        {
            Consultation = new NhAssistantApprovalConsultationDto(consultationId, "project-reviewer", "project", "p-1", "Project 1")
            {
                LeaveRoute = NhAssistantConsultationRoutes.SubjectAgentRights,
                Answer = "It is ready."
            }
        };

        var json = JsonSerializer.SerializeToElement(part, NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);
        var own = JsonSerializer.SerializeToElement(part with { Consultation = null }, NhAssistantJsonSerializerContext.Default.NhAssistantMessagePartDto);

        Assert.Equal("approval", json.GetProperty("type").GetString());
        var consultation = json.GetProperty("consultation");
        Assert.Equal(
            ["consultationId", "agentId", "subjectType", "subjectId", "subjectDisplayName", "leaveRoute", "answer", "movedToApprovalId"],
            consultation.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(consultationId, consultation.GetProperty("consultationId").GetGuid());
        Assert.Equal("subject-agent-rights", consultation.GetProperty("leaveRoute").GetString());
        Assert.False(own.TryGetProperty("consultation", out _));
    }

    [Fact]
    public void Proposals_and_consultations_carry_their_routes()
    {
        var proposal = new NhAssistantProposalDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "hash",
            Guid.NewGuid(),
            null,
            "project-reviewer",
            "project",
            "p-1",
            null,
            "projects.change-status",
            "summary",
            null,
            "{}",
            [],
            "pending",
            "deferred",
            null,
            "agent",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "member-1",
            null,
            null,
            null,
            null)
        {
            ConsultationId = Guid.NewGuid(),
            ConsultationRoute = NhAssistantConsultationRoutes.SubjectAgentRights
        };
        var consultation = new NhAssistantConsultationDto(
            Guid.NewGuid(),
            "member-1",
            null,
            "project-assistant",
            1,
            "completed",
            null,
            1,
            10,
            10,
            2,
            DateTimeOffset.UnixEpoch,
            null)
        {
            ProposalsByRoute = new Dictionary<string, int>
            {
                [NhAssistantConsultationRoutes.AskerDecides] = 1,
                [NhAssistantConsultationRoutes.SubjectAgentRights] = 1
            }
        };

        var proposalJson = JsonSerializer.SerializeToElement(proposal, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalDto);
        var consultationJson = JsonSerializer.SerializeToElement(
            new NhAssistantConsultationListDto([consultation], 1),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantConsultationListDto);

        Assert.Equal("subject-agent-rights", proposalJson.GetProperty("consultationRoute").GetString());
        var byRoute = consultationJson.GetProperty("items")[0].GetProperty("proposalsByRoute");
        Assert.Equal(1, byRoute.GetProperty("asker-decides").GetInt32());
        Assert.Equal(1, byRoute.GetProperty("subject-agent-rights").GetInt32());
    }

    [Fact]
    public void The_consult_answer_and_the_route_switches_read_and_write_their_fields()
    {
        var approvalId = Guid.NewGuid();
        var answer = JsonSerializer.SerializeToElement(
            new NhAssistantConsultationAnswer(Guid.NewGuid(), "Ready.", 1)
            {
                Decision = NhAssistantConsultationDecisions.Asker,
                ApprovalIds = [approvalId]
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = JsonSerializer.Deserialize(
            """{"askerDecides":"on","subjectAgentRights":"off","subjectAskerRights":"code","reason":"Pilot"}""",
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantConsultationRoutesRequest)!;

        Assert.Equal("asker", answer.GetProperty("decision").GetString());
        Assert.Equal(approvalId, answer.GetProperty("approvalIds")[0].GetGuid());
        Assert.Equal(NhAssistantRouteSwitches.On, request.AskerDecides);
        Assert.Equal(NhAssistantRouteSwitches.Off, request.SubjectAgentRights);
        Assert.Equal(NhAssistantRouteSwitches.Code, request.SubjectAskerRights);
    }
}
