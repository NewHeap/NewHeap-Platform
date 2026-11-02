using System.Globalization;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The administration of alert kinds (FB87 phase 2): every registered kind with what its code declares
/// and allows, the administrator's settings, what applies now and the numbers of its alerts; and the
/// change of one kind's settings, checked against the bounds of its code and saved as the next version.
/// A change the code does not allow is refused with a stable code and nothing is saved; a value that
/// repeats the code's is accepted, and a delivery equal to the code's is stored as the code's, so the
/// kind keeps following its code. Every change is audited as <see cref="NhAssistantAuditEventKind.AdminAlertKindChanged"/>
/// with the names of the settings that changed, never their values.
/// </summary>
internal sealed class NhAssistantAdminAlertKinds(
    NhAssistantRegistrationState registration,
    NhAssistantAlertKindSettings kindSettings,
    NhAssistantAlertKindSettingStore store,
    NhAssistantAlertNumbersStore numbers,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentEvents events,
    TimeProvider? timeProvider = null)
{
    private const int MaxActorIdLength = 256;

    /// <summary>
    /// The application's clock, which alerts and the time of a change follow; the audit keeps the system clock.
    /// </summary>
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>The moment on the application's clock that a period without an end ends at.</summary>
    public DateTimeOffset Now => _applicationClock.GetUtcNow();

    public async Task<NhAssistantAdminAlertKindsDto> GetAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var states = await kindSettings.GetAllAsync(cancellationToken);
        var aggregates = await numbers.GetAsync(states.Keys.ToArray(), from, to, cancellationToken);

        var agentNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var agentId in aggregates.Shadow.Select(row => row.AgentId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
            agentNames[agentId] = agent?.Definition.DisplayNameKey;
        }

        var kinds = states.Values
            .OrderBy(state => state.Kind.Id, StringComparer.Ordinal)
            .Select(state => ToDto(state) with
            {
                Numbers = NumbersOf(state.Kind.Id, aggregates),
                Shadow = aggregates.Shadow
                    .Where(row => row.KindId == state.Kind.Id && row.AgentId is not null)
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.AgentId, StringComparer.Ordinal)
                    .Select(row => new NhAssistantAdminAlertShadowAgentDto(row.AgentId!, agentNames.GetValueOrDefault(row.AgentId!), row.Count))
                    .ToArray()
            })
            .ToArray();
        return new NhAssistantAdminAlertKindsDto(from, to, kinds);
    }

    /// <summary>
    /// Checks the change against the kind's code and saves it as the settings' next version, or refuses it
    /// with the code of the first thing the code does not allow. The same settings again change nothing.
    /// </summary>
    public async Task<TaskResult<NhAssistantAdminAlertKindDto>> SaveAsync(
        string kindId,
        NhAssistantAdminAlertKindRequest request,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kindId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        if (!registration.AlertKinds.TryGetValue(kindId, out var kind))
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(NhAssistantAlertCodes.KindUnknown, "The alert kind is not registered.");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var errors = new NhAssistantValidationErrors()
            .Require(request.Enabled is not null, "enabled", NhAssistantFieldErrors.Required)
            .Require(request.ExpectedVersion is null or >= 0, "expectedVersion", NhAssistantFieldErrors.Invalid)
            .Require(reason is null || reason.Length <= AssistantAlertKindSetting.MaxReasonLength, "reason", NhAssistantFieldErrors.TooLong);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(errors.ToResult());
        }
        if (request.Enabled == false && !NhAssistantAlertKindSettings.MaySwitchOff(kind))
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(
                NhAssistantAdminErrorCodes.AlertKindSwitchOffNotAllowed,
                NhAssistantErrorCodes.MessageKey(NhAssistantAdminErrorCodes.AlertKindSwitchOffNotAllowed));
        }

        var delivery = ReadDelivery(request.Delivery);
        if (!delivery.Success)
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(delivery);
        }

        var code = NhAssistantAlertEffectiveDelivery.Of(kind.Delivery);
        var wanted = delivery.Data;
        if (wanted is not null && CheckBounds(kind, code, wanted) is { } notAllowed)
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(notAllowed);
        }

        // A delivery equal to the code's keeps the kind following its code.
        if (wanted is not null && SameDelivery(wanted, code))
        {
            wanted = null;
        }

        var current = (await kindSettings.GetAsync(kind, cancellationToken)).Stored;
        if ((current?.Version ?? 0) != (request.ExpectedVersion ?? 0))
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The alert kind's settings changed in the meantime.");
        }

        var settings = new AssistantAlertKindSetting
        {
            KindId = kind.Id,
            Enabled = request.Enabled!.Value,
            UpdatedAt = _applicationClock.GetUtcNow(),
            UpdatedBy = actorId,
            Reason = reason
        };
        Apply(settings, wanted);
        var changed = ChangedFields(current, settings);
        if (changed.Count == 0)
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Succeeded(ToDto(NhAssistantAlertKindSettings.Merge(kind, current)));
        }

        var saved = await store.SaveAsync(settings, current?.Version ?? 0, cancellationToken);
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantAdminAlertKindDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The alert kind's settings changed in the meantime.");
        }

        kindSettings.Forget();
        await events.AlertKindChangedAsync(
            kind.Id,
            actorId,
            saved.Value!.Before is null ? "created" : "changed",
            changed,
            cancellationToken);
        return TaskResult<NhAssistantAdminAlertKindDto>.Succeeded(ToDto(NhAssistantAlertKindSettings.Merge(kind, saved.Value.Saved)));
    }

    /// <summary>
    /// The kind as the administration shows it, without numbers.
    /// </summary>
    internal static NhAssistantAdminAlertKindDto ToDto(NhAssistantAlertKindState state)
    {
        var kind = state.Kind;
        var allowed = kind.AdministratorChoices;
        var stored = state.Stored;
        return new NhAssistantAdminAlertKindDto(
            kind.Id,
            kind.DisplayNameKey,
            NhAssistantAlertUrgencyNames.Of(kind.Urgency),
            kind.Required,
            new NhAssistantAdminAlertKindChoicesDto(
                NhAssistantAlertKindSettings.MaySwitchOff(kind),
                allowed.HasFlag(NhAssistantAlertAdminChoices.DigestTimes),
                allowed.HasFlag(NhAssistantAlertAdminChoices.WorkdaysOnly),
                allowed.HasFlag(NhAssistantAlertAdminChoices.ImmediatelyFrom),
                allowed.HasFlag(NhAssistantAlertAdminChoices.QuietHours)),
            ToDto(NhAssistantAlertEffectiveDelivery.Of(kind.Delivery)),
            new NhAssistantAdminAlertKindSettingsDto(
                stored?.Enabled ?? true,
                StoredDeliveryOf(kind.Id, stored) is { } own ? ToDto(own) : null,
                stored?.Version ?? 0,
                stored?.UpdatedBy,
                stored?.UpdatedAt,
                stored?.Reason),
            state.Enabled,
            ToDto(state.Delivery));
    }

    internal static NhAssistantAdminAlertDeliveryDto ToDto(NhAssistantAlertEffectiveDelivery delivery)
    {
        return new NhAssistantAdminAlertDeliveryDto(
            NhAssistantAlertKindSettingModes.Of(delivery.Mode),
            delivery.DigestTimes.Select(Format).ToArray(),
            delivery.WorkdaysOnly,
            delivery.ImmediatelyFrom is { } from ? NhAssistantAlertUrgencyNames.Of(from) : null,
            delivery.QuietHours is { } quiet ? new NhAssistantAdminAlertQuietHoursDto(Format(quiet.Start), Format(quiet.End)) : null);
    }

    private static NhAssistantAdminAlertKindNumbersDto NumbersOf(string kindId, NhAssistantAlertKindAggregates aggregates)
    {
        var rows = aggregates.Deliveries.Where(row => row.KindId == kindId).ToArray();
        int Count(Func<NhAssistantAlertDeliveryCount, bool> predicate)
        {
            return rows.Where(predicate).Sum(row => row.Count);
        }

        return new NhAssistantAdminAlertKindNumbersDto(
            aggregates.Raised.Where(row => row.KindId == kindId).Sum(row => row.Count),
            Count(row => row.Delivered),
            Count(row => row.Read),
            Count(row => row.Dismissed),
            Count(row => row.ResolvedBeforeReading),
            Count(row => row.Withheld),
            Count(row => row.Muted));
    }

    /// <summary>
    /// The administrator's delivery as stored, before the code's bounds apply; <see langword="null"/> for the code's.
    /// </summary>
    private static NhAssistantAlertEffectiveDelivery? StoredDeliveryOf(string kindId, AssistantAlertKindSetting? stored)
    {
        if (stored?.DeliveryMode is null || !NhAssistantAlertKindSettingModes.TryParse(stored.DeliveryMode, out var mode))
        {
            return null;
        }

        var quietHours = stored.QuietHoursStart is { } start && stored.QuietHoursEnd is { } end
            ? new NhAssistantAlertQuietHours(start, end)
            : null;
        return mode == NhAssistantAlertDeliveryMode.DailyDigest
            ? new NhAssistantAlertEffectiveDelivery(mode, NhAssistantAlertKindSettings.ParseTimes(kindId, stored.DigestTimes), stored.WorkdaysOnly, stored.ImmediatelyFrom, quietHours)
            : NhAssistantAlertEffectiveDelivery.Immediate(quietHours);
    }

    /// <summary>
    /// Reads the delivery of a change; <see langword="null"/> for the code's. A malformed one fails with
    /// <see cref="NhAssistantAdminErrorCodes.AlertKindDeliveryInvalid"/> and the fields that are wrong.
    /// </summary>
    private static TaskResult<NhAssistantAlertEffectiveDelivery?> ReadDelivery(NhAssistantAdminAlertDeliveryRequest? request)
    {
        if (request is null)
        {
            return TaskResult<NhAssistantAlertEffectiveDelivery?>.Succeeded(null);
        }

        var errors = new NhAssistantValidationErrors();
        var modeKnown = NhAssistantAlertKindSettingModes.TryParse(request.Mode, out var mode);
        errors.Require(modeKnown, "delivery.mode", request.Mode is null ? NhAssistantFieldErrors.Required : NhAssistantFieldErrors.Invalid);

        var times = new List<TimeOnly>();
        foreach (var value in request.DigestTimes ?? [])
        {
            if (value is null || !NhAssistantAlertPreferences.TryParseDigestTime(value, out var time) || times.Contains(time))
            {
                errors.Add("delivery.digestTimes", NhAssistantFieldErrors.Invalid);
                break;
            }

            times.Add(time);
        }

        NhAssistantAlertUrgency? immediatelyFrom = null;
        if (request.ImmediatelyFrom is not null)
        {
            if (NhAssistantAlertUrgencyNames.TryParse(request.ImmediatelyFrom, out var urgency))
            {
                immediatelyFrom = urgency;
            }
            else
            {
                errors.Add("delivery.immediatelyFrom", NhAssistantFieldErrors.Invalid);
            }
        }

        NhAssistantAlertQuietHours? quietHours = null;
        if (request.QuietHours is { } quiet)
        {
            if (quiet.Start is not null
                && quiet.End is not null
                && NhAssistantAlertPreferences.TryParseDigestTime(quiet.Start, out var start)
                && NhAssistantAlertPreferences.TryParseDigestTime(quiet.End, out var end)
                && start != end)
            {
                quietHours = new NhAssistantAlertQuietHours(start, end);
            }
            else
            {
                errors.Add("delivery.quietHours", NhAssistantFieldErrors.Invalid);
            }
        }

        if (modeKnown && mode == NhAssistantAlertDeliveryMode.DailyDigest)
        {
            errors.Require(times.Count is >= 1 and <= AssistantAlertKindSetting.MaxDigestTimes, "delivery.digestTimes", NhAssistantFieldErrors.Invalid);
        }
        else if (modeKnown)
        {
            // Delivered immediately: no digests, so no digest times, working days or urgency from which.
            errors
                .Require(times.Count == 0, "delivery.digestTimes", NhAssistantFieldErrors.Invalid)
                .Require(request.WorkdaysOnly != true, "delivery.workdaysOnly", NhAssistantFieldErrors.Invalid)
                .Require(request.ImmediatelyFrom is null, "delivery.immediatelyFrom", NhAssistantFieldErrors.Invalid);
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAlertEffectiveDelivery?>.Failed(errors.ToResult(
                NhAssistantAdminErrorCodes.AlertKindDeliveryInvalid,
                NhAssistantErrorCodes.MessageKey(NhAssistantAdminErrorCodes.AlertKindDeliveryInvalid)));
        }

        return TaskResult<NhAssistantAlertEffectiveDelivery?>.Succeeded(mode == NhAssistantAlertDeliveryMode.DailyDigest
            ? new NhAssistantAlertEffectiveDelivery(mode, times.Order().ToArray(), request.WorkdaysOnly == true, immediatelyFrom, quietHours)
            : NhAssistantAlertEffectiveDelivery.Immediate(quietHours));
    }

    /// <summary>
    /// The refusal of the parts of <paramref name="wanted"/> that differ from the code's delivery where the
    /// code does not let an administrator change them; <see langword="null"/> when the code allows them all.
    /// </summary>
    private static TaskResult? CheckBounds(
        NhAssistantAlertKind kind,
        NhAssistantAlertEffectiveDelivery code,
        NhAssistantAlertEffectiveDelivery wanted)
    {
        var allowed = kind.AdministratorChoices;
        var errors = new NhAssistantValidationErrors()
            .Require(
                allowed.HasFlag(NhAssistantAlertAdminChoices.DigestTimes)
                    || (wanted.Mode == code.Mode && wanted.DigestTimes.SequenceEqual(code.DigestTimes)),
                "delivery.digestTimes",
                NhAssistantFieldErrors.NotAllowed)
            .Require(
                allowed.HasFlag(NhAssistantAlertAdminChoices.QuietHours) || wanted.QuietHours == code.QuietHours,
                "delivery.quietHours",
                NhAssistantFieldErrors.NotAllowed);

        // Working days and the urgency from which a digest is skipped apply to digests only.
        if (wanted.Mode == NhAssistantAlertDeliveryMode.DailyDigest)
        {
            errors
                .Require(
                    allowed.HasFlag(NhAssistantAlertAdminChoices.WorkdaysOnly) || wanted.WorkdaysOnly == code.WorkdaysOnly,
                    "delivery.workdaysOnly",
                    NhAssistantFieldErrors.NotAllowed)
                .Require(
                    allowed.HasFlag(NhAssistantAlertAdminChoices.ImmediatelyFrom) || wanted.ImmediatelyFrom == code.ImmediatelyFrom,
                    "delivery.immediatelyFrom",
                    NhAssistantFieldErrors.NotAllowed);
        }

        return errors.IsEmpty
            ? null
            : errors.ToResult(
                NhAssistantAdminErrorCodes.AlertKindDeliveryNotAllowed,
                NhAssistantErrorCodes.MessageKey(NhAssistantAdminErrorCodes.AlertKindDeliveryNotAllowed));
    }

    private static bool SameDelivery(NhAssistantAlertEffectiveDelivery left, NhAssistantAlertEffectiveDelivery right)
    {
        return left.Mode == right.Mode
            && left.DigestTimes.SequenceEqual(right.DigestTimes)
            && left.WorkdaysOnly == right.WorkdaysOnly
            && left.ImmediatelyFrom == right.ImmediatelyFrom
            && left.QuietHours == right.QuietHours;
    }

    private static void Apply(AssistantAlertKindSetting settings, NhAssistantAlertEffectiveDelivery? delivery)
    {
        if (delivery is null)
        {
            return;
        }

        settings.DeliveryMode = NhAssistantAlertKindSettingModes.Of(delivery.Mode);
        settings.DigestTimes = delivery.DigestTimes.Count == 0 ? null : NhAssistantAlertKindSettings.FormatTimes(delivery.DigestTimes);
        settings.WorkdaysOnly = delivery.WorkdaysOnly;
        settings.ImmediatelyFrom = delivery.ImmediatelyFrom;
        settings.QuietHoursStart = delivery.QuietHours?.Start;
        settings.QuietHoursEnd = delivery.QuietHours?.End;
    }

    /// <summary>
    /// The names of the settings that differ between what is stored and the change, for the audit; the
    /// reason alone is no change.
    /// </summary>
    private static IReadOnlyList<string> ChangedFields(AssistantAlertKindSetting? before, AssistantAlertKindSetting after)
    {
        var current = before ?? new AssistantAlertKindSetting { KindId = after.KindId };
        var changed = new List<string>();
        if (current.Enabled != after.Enabled)
        {
            changed.Add("enabled");
        }
        if (!string.Equals(current.DeliveryMode, after.DeliveryMode, StringComparison.Ordinal))
        {
            changed.Add("delivery-mode");
        }
        if (!string.Equals(current.DigestTimes, after.DigestTimes, StringComparison.Ordinal))
        {
            changed.Add("digest-times");
        }
        if (current.WorkdaysOnly != after.WorkdaysOnly)
        {
            changed.Add("workdays-only");
        }
        if (current.ImmediatelyFrom != after.ImmediatelyFrom)
        {
            changed.Add("immediately-from");
        }
        if (current.QuietHoursStart != after.QuietHoursStart || current.QuietHoursEnd != after.QuietHoursEnd)
        {
            changed.Add("quiet-hours");
        }

        return changed;
    }

    private static string Format(TimeOnly time)
    {
        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
