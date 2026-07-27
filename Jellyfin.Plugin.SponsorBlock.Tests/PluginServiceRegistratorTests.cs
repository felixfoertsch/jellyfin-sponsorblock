using Jellyfin.Plugin.SponsorBlock.Scanning;
using Jellyfin.Plugin.SponsorBlock.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Jellyfin.Plugin.SponsorBlock.Tests;

public class PluginServiceRegistratorTests
{
	[Fact]
	public void RegisterServices_RegistersSponsorBlockRefreshTask()
	{
		var services = new ServiceCollection();

		new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

		Assert.Contains(services, descriptor =>
			descriptor.ServiceType == typeof(IScheduledTask)
			&& descriptor.ImplementationType == typeof(SponsorBlockRefreshTask));
	}

	[Fact]
	public void RegisterServices_RegistersForceScanService()
	{
		var services = new ServiceCollection();

		new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

		Assert.Contains(services, descriptor =>
			descriptor.ServiceType == typeof(IForceScanService)
			&& descriptor.Lifetime == ServiceLifetime.Singleton);
	}

	[Fact]
	public void RegisterServices_RegistersSponsorBlockSegmentProviderAsSingleton()
	{
		var services = new ServiceCollection();

		new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

		Assert.Contains(services, descriptor =>
			descriptor.ServiceType == typeof(IMediaSegmentProvider)
			&& descriptor.ImplementationType == typeof(SponsorBlockSegmentProvider)
			&& descriptor.Lifetime == ServiceLifetime.Singleton);
	}
}
