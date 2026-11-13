using System;
using System.Collections.Generic;
using System.Linq;
using FastSTRM.Configuration;
using FastSTRM.Playback;
using FastSTRM.Probing;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FastSTRM.Api;

[ApiController]
[Route("FastStrm")]
[Authorize(Policy = "RequiresElevation")]
public class FastStrmController : ControllerBase
{
    private readonly ProbeService _probeService;
    private readonly PlaybackPolicyResolver _policyResolver;
    private readonly IUserManager _userManager;

    public FastStrmController(
        ProbeService probeService,
        PlaybackPolicyResolver policyResolver,
        IUserManager userManager)
    {
        _probeService = probeService;
        _policyResolver = policyResolver;
        _userManager = userManager;
    }

    [HttpGet("Summary")]
    public ActionResult GetSummary([FromQuery] bool refresh = false)
    {
        var (total, probed, unprobed, sizeMissing) = _probeService.GetSummary(refresh);

        var global = _policyResolver.GlobalPolicy;
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return Ok(new
        {
            Total = total,
            Probed = probed,
            Unprobed = unprobed,
            SizeMissing = sizeMissing,
            SkipProbeForUnprobedStrm = global.SkipProbe,
            EnableBackgroundProbeForUnprobedStrm = global.EnableBackgroundProbe,
            BackgroundProbeDelaySeconds = global.BackgroundProbeDelaySeconds,
            CancelBackgroundProbeOnPlaybackStop = global.CancelBackgroundProbeOnPlaybackStop,
            RemoteSizeTimeoutSeconds = configuration.GetRemoteSizeTimeoutSeconds(),
            AllowPrivateNetworkTargets = configuration.AllowPrivateNetworkTargets
        });
    }

    [HttpGet("PlaybackPolicy")]
    public ActionResult GetPlaybackPolicies([FromQuery] Guid? userId)
    {
        var configured = _policyResolver.GetConfiguredPolicies();

        var users = new List<object>();
        var knownUserIds = new HashSet<Guid>();

        foreach (var user in _userManager.GetUsers())
        {
            knownUserIds.Add(user.Id);

            if (userId.HasValue && userId.Value != user.Id)
            {
                continue;
            }

            users.Add(BuildUserPolicyView(user));
        }

        var orphans = configured
            .Where(policy => !knownUserIds.Contains(policy.UserId))
            .Select(policy => new
            {
                policy.UserId,
                policy.Mode,
                policy.BackgroundProbeDelaySeconds
            })
            .ToList();

        return Ok(new
        {
            Global = ToPolicyView(_policyResolver.GlobalPolicy),
            Users = users,
            Orphans = orphans,
            PoliciesTruncated = _policyResolver.PoliciesTruncated
        });
    }

    [HttpGet("PlaybackPolicy/Effective")]
    public ActionResult GetEffectivePlaybackPolicy([FromQuery] Guid? userId)
        => Ok(ToPolicyView(_policyResolver.Resolve(userId)));

    [HttpPost("Probe/Start")]
    public ActionResult StartProbe([FromQuery] string mode = ProbeService.IncrementalMode)
    {
        if (!ProbeService.TryParseMode(mode, out var normalizedMode))
        {
            return BadRequest(new
            {
                Message = $"Unsupported mode '{mode}'. Use '{ProbeService.IncrementalMode}' "
                          + $"or '{ProbeService.FullMode}'."
            });
        }

        return _probeService.StartProbe(normalizedMode)
            ? NoContent()
            : Conflict(new { Message = "A probe task is already running or still stopping." });
    }

    [HttpPost("Probe/Stop")]
    public ActionResult StopProbe()
    {
        _probeService.StopProbe();
        return NoContent();
    }

    [HttpGet("Probe/Status")]
    public ActionResult GetProbeStatus()
        => Ok(new
        {
            _probeService.State,
            Processed = _probeService.ProcessedItems,
            Total = _probeService.TotalItems,
            CurrentItem = _probeService.CurrentItemName
        });

    private object BuildUserPolicyView(User user)
    {
        var hasEntry = _policyResolver.TryGetConfigured(user.Id, out var configured);
        var effective = _policyResolver.Resolve(user.Id);

        string? configuredMode = hasEntry ? configured?.Mode : null;

        var modeIsValid = PlaybackPolicyMode.TryNormalize(configuredMode, out var normalizedMode);
        var inheritsExplicitly = modeIsValid
                                 && string.Equals(normalizedMode, PlaybackPolicyMode.Inherit, StringComparison.Ordinal);

        return new
        {
            UserId = user.Id,
            UserName = GetUserName(user),
            HasOverride = hasEntry && modeIsValid && !inheritsExplicitly,
            ConfiguredMode = configuredMode,
            ConfiguredModeValid = hasEntry && modeIsValid,
            ConfiguredDelaySeconds = hasEntry ? configured?.BackgroundProbeDelaySeconds : null,
            Effective = ToPolicyView(effective)
        };
    }

    private static object ToPolicyView(EffectivePlaybackPolicy policy)
        => new
        {
            policy.SkipProbe,
            policy.EnableBackgroundProbe,
            policy.BackgroundProbeDelaySeconds,
            policy.CancelBackgroundProbeOnPlaybackStop,
            Mode = policy.Mode,
            policy.Source,
            policy.FallbackReason
        };

    private string GetUserName(User user)
    {
        try
        {
            var dto = _userManager.GetUserDto(user, null);
            if (!string.IsNullOrEmpty(dto?.Name))
            {
                return dto.Name;
            }
        }
        catch (Exception)
        {
        }

        return user.Id.ToString();
    }
}
