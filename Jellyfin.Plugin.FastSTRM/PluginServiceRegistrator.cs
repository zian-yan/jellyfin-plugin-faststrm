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

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp => new RemoteSizeService(
            sp.GetRequiredService<ILibraryManager>(),
            sp.GetRequiredService<ILogger<RemoteSizeService>>()));

        serviceCollection.AddSingleton<ProbeService>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProbeService>());

        serviceCollection.AddSingleton<PlaybackPolicyResolver>();

        DecorateService<IMediaSourceManager>(
            serviceCollection,
            (sp, inner, ownsInner) => new MediaSourceInterceptor(
                inner,
                sp.GetRequiredService<ILogger<MediaSourceInterceptor>>(),
                sp,
                sp.GetRequiredService<PlaybackPolicyResolver>(),
                sp.GetRequiredService<RemoteSizeService>(),
                ownsInner));

        DecorateService<IUserDataManager>(
            serviceCollection,
            (sp, inner, ownsInner) => new PlaybackStateInterceptor(
                inner,
                sp,
                sp.GetRequiredService<ILogger<PlaybackStateInterceptor>>(),
                sp.GetRequiredService<PlaybackPolicyResolver>(),
                ownsInner));
    }

    private static void DecorateService<TService>(
        IServiceCollection serviceCollection,
        Func<IServiceProvider, TService, bool, TService> decoratorFactory)
        where TService : class
    {
        var descriptor = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is null)
        {
            return;
        }

        serviceCollection.Remove(descriptor);
        serviceCollection.Add(new ServiceDescriptor(
            typeof(TService),
            sp => decoratorFactory(sp, CreateInstance<TService>(sp, descriptor, out var ownsInner), ownsInner),
            descriptor.Lifetime));
    }

    private static TService CreateInstance<TService>(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor,
        out bool owned)
        where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance)
        {
            owned = false;
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            owned = false;
            return (TService)descriptor.ImplementationFactory(serviceProvider);
        }

        if (descriptor.ImplementationType is not null)
        {
            owned = true;
            return (TService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            $"The original {typeof(TService).FullName} registration cannot be resolved.");
    }
}
