using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-320: the planner's risk review is a skill defined as data whose instructions are the sample's
/// own text asset. The administration lists the sample's texts and reads one read-only; a save stores
/// the asset's id, version and hash and is refused for another hash; version 2 of the text takes effect
/// only as the skill's next version, saved explicitly. Only administrators see the texts.
/// </summary>
public sealed class AssistantInstructionAssetSamplesTests(AssistantAgentRunSampleHost host)
    : IClassFixture<AssistantAgentRunSampleHost>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SPM_320_the_planners_risk_review_takes_its_instructions_from_the_samples_text_asset()
    {
        const string planner = SampleProjectPlannerComposition.AgentId;
        const string skillId = SampleInstructionAssetService.RiskReviewSkill;
        var first = SampleInstructionAssets.RiskReviewV1;
        var second = SampleInstructionAssets.RiskReviewV2;
        using var administrator = host.CreateClient(host.OwnerId.ToString(), admin: true);
        using var colleague = host.CreateClient(host.ColleagueId.ToString());
        try
        {
            // The administration offers the sample's texts, without their content; version 1 is
            // deprecated now that version 2 exists.
            var catalog = (await administrator.GetFromJsonAsync<NhAssistantRunCatalogDto>("/api/assistant/admin/run-catalog", Json))!;
            Assert.True(catalog.InstructionAssets);
            var list = (await administrator.GetFromJsonAsync<NhAssistantInstructionAssetListDto>("/api/assistant/admin/instruction-assets", Json))!;
            Assert.True(list.Supported);
            Assert.Equal(
                [
                    (SampleInstructionAssetComposition.RiskReviewAsset, 1, first.Manifest.ContentHash, true),
                    (SampleInstructionAssetComposition.RiskReviewAsset, 2, second.Manifest.ContentHash, false),
                    (SampleInstructionAssetComposition.RiskReviewAsset, 3, SampleInstructionAssets.RiskReviewV3.Manifest.ContentHash, false)
                ],
                list.Assets.Select(asset => (asset.AssetId, asset.Version, asset.Hash, asset.DeprecatedAt is not null)).ToArray());
            var text = (await administrator.GetFromJsonAsync<NhAssistantInstructionAssetTextDto>(
                $"/api/assistant/admin/instruction-assets/{SampleInstructionAssetComposition.RiskReviewAsset}/versions/2",
                Json))!;
            Assert.Equal((second.Content, second.Manifest.ContentHash, (string?)null), (text.Content, text.Hash, text.Problem));

            // A reference with another hash than the text's is refused, so the skill never names a text
            // the administrator did not read.
            using (var refused = await administrator.PutAsJsonAsync(
                $"/api/assistant/admin/agents/{planner}/skills/{skillId}",
                new
                {
                    description = "Reviews deadline risks.",
                    instructionsAsset = new { assetId = SampleInstructionAssetComposition.RiskReviewAsset, version = 1, hash = new string('0', 64) }
                },
                Json))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                var errors = (await refused.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("errors");
                Assert.Equal("invalid", errors.GetProperty("instructionsAsset.hash")[0].GetString());
            }

            // The application adds the risk review with version 1 of its text: the skill stores the
            // reference, never the text, and is in effect in shadow.
            var added = await ConfigureAsync(service => service.AddRiskReviewAsync(host.OwnerId, CancellationToken.None));
            Assert.True(added.Success);
            Assert.Equal(SampleInstructionAssets.ReferenceOf(first), added.Data!.InstructionsAsset);
            Assert.Null(added.Data.Instructions);
            Assert.Equal((first.Manifest.ContentHash, true), (added.Data.InstructionsHash, added.Data.InEffect));
            var configured = (await administrator.GetFromJsonAsync<NhAssistantAgentConfigurationDto>($"/api/assistant/admin/agents/{planner}/configuration", Json))!;
            var skill = Assert.Single(configured.DataSkills, item => item.SkillId == skillId);
            Assert.Equal((SampleInstructionAssets.ReferenceOf(first), true, (string?)null), (skill.InstructionsAsset, skill.InEffect, skill.Problem));

            // Version 2 of the text is used only once an administrator saves it as the skill's next
            // version; the earlier version keeps naming version 1 in the history.
            var moved = await ConfigureAsync(service => service.UseRiskReviewVersionAsync(second, host.OwnerId, CancellationToken.None));
            Assert.True(moved.Success);
            Assert.Equal((added.Data.Version + 1, SampleInstructionAssets.ReferenceOf(second)), (moved.Data!.Version, moved.Data.InstructionsAsset));
            var versions = (await administrator.GetFromJsonAsync<NhAssistantDataSkillVersionDto[]>($"/api/assistant/admin/agents/{planner}/skills/{skillId}/versions", Json))!;
            Assert.Equal(
                [(moved.Data.Version, NhAssistantSkillDefinitionStatuses.Live, 2), (added.Data.Version, NhAssistantSkillDefinitionStatuses.Superseded, 1)],
                versions.Take(2).Select(version => (version.Version, version.Status, version.InstructionsAsset!.Version)).ToArray());

            // People who do not administer see none of the texts.
            using var forbidden = await colleague.GetAsync("/api/assistant/admin/instruction-assets");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal(NhAssistantAdminErrorCodes.Forbidden, (await forbidden.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        }
        finally
        {
            // Other samples share the host and its planner.
            Assert.True((await ConfigureAsync(service => service.RemoveRiskReviewAsync(host.OwnerId, CancellationToken.None))).Success);
        }
    }

    private async Task<TaskResult> ConfigureAsync(Func<SampleInstructionAssetService, Task<TaskResult>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleInstructionAssetService>());
    }

    private async Task<TaskResult<T>> ConfigureAsync<T>(Func<SampleInstructionAssetService, Task<TaskResult<T>>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SampleInstructionAssetService>());
    }
}
