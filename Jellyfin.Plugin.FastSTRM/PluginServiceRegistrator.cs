using System;
using System.Linq;
using FastSTRM.Playback;
using FastSTRM.Probing;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastSTRM;

/// <summary>
/// Registers plugin services via Jellyfin's DI pipeline.
/// Decorates <see cref="IMediaSourceManager"/> with <see cref="MediaSourceInterceptor"/>,
/// decorates <see cref="IUserDataManager"/> with <see cref="PlaybackStateInterceptor"/>,
/// registers <see cref="ProbeService"/> as a hosted singleton and
/// <see cref="PlaybackPolicyResolver"/> as the playback-policy singleton both decorators share.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Resolves the real (remote) size of a .strm target; shared by the probe service and the
        // media-source decorator, so both see the same cache.
        serviceCollection.AddSingleton<RemoteSizeService>();

        // Register ProbeService as singleton + hosted service for ItemAdded event lifecycle
        serviceCollection.AddSingleton<ProbeService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProbeService>());

        // Single source of truth for "which playback policy applies to this user".
        // Both interceptors resolve through it instead of reading the configuration directly.
        serviceCollection.AddSingleton<PlaybackPolicyResolver>();

        // Decorate IMediaSourceManager to skip ffprobe for .strm playback.
        DecorateService<IMediaSourceManager>(
            serviceCollection,
            (sp, inner, ownsInner) => new MediaSourceInterceptor(
                inner,
                sp.GetRequiredService<ILogger<MediaSourceInterceptor>>(),
                sp,
                sp.GetRequiredService<PlaybackPolicyResolver>(),
                sp.GetRequiredService<RemoteSizeService>(),
                ownsInner));

        // Decorate IUserDataManager so progress reported for an item that is still being
        // probed is not thrown away by Jellyfin's "unknown runtime means fully played" rule.
        DecorateService<IUserDataManager>(
            serviceCollection,
            (sp, inner, ownsInner) => new PlaybackStateInterceptor(
                inner,
                sp,
                sp.GetRequiredService<ILogger<PlaybackStateInterceptor>>(),
                sp.GetRequiredService<PlaybackPolicyResolver>(),
                ownsInner));
    }

    /// <summary>
    /// Replaces a service registration with a decorator that wraps the original implementation.
    /// </summary>
    private static void DecorateService<TService>(
        IServiceCollection serviceCollection,
        Func<IServiceProvider, TService, bool, TService> decoratorFactory)
        where TService : class
    {
        var descriptor = serviceCollection.FirstOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is null)
        {
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.Add(new ServiceDescriptor(
            typeof(TService),
            sp =>
            {
                var (inner, ownsInner) = CreateInstance<TService>(sp, descriptor);
                return decoratorFactory(sp, inner, ownsInner);
            },
            descriptor.Lifetime));
    }

    private static (TService Instance, bool Owned) CreateInstance<TService>(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
        where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance)
        {
            // The container owns this instance; disposing it from the decorator would be wrong.
            return (instance, false);
        }

        if (descriptor.ImplementationFactory is not null)
        {
            // A factory can return a cached or shared instance, so its ownership is unknown.
            // The decorator must not dispose it.
            return ((TService)descriptor.ImplementationFactory(serviceProvider), false);
        }

        if (descriptor.ImplementationType is not null)
        {
            // ActivatorUtilities creates the instance outside the container's tracking, so the
            // decorator is the only owner and must dispose it.
            return (
                (TService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType),
                true);
        }

        throw new InvalidOperationException(
            $"The original {typeof(TService).FullName} registration cannot be resolved.");
    }
}
