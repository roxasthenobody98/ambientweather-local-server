﻿using Common;
using Common.Observe;
using Core.GitHub.Dto;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace Core.GitHub;

public interface IGitHubService
{
	Task<AmbientWeatherServerLatestRelease> GetLatestReleaseAsync();
}

public class GitHubService : IGitHubService
{
	private static readonly ILogger _logger = LogContext.ForClass<GitHubService>();
	private static readonly object _lock = new object();

	private const string LatestReleaseKey = "GithubLatestRelease";

	private readonly IGitHubApiClient _apiClient;
	private readonly IMemoryCache _cache;

	public GitHubService(IGitHubApiClient apiClient, IMemoryCache cache)
	{
		_apiClient = apiClient;
		_cache = cache;
	}

	public Task<AmbientWeatherServerLatestRelease> GetLatestReleaseAsync()
	{
		using var tracing = Tracing.Trace($"{nameof(GitHubService)}.{nameof(GetLatestReleaseAsync)}");

		lock (_lock)
		{
			return _cache.GetOrCreateAsync(LatestReleaseKey, async (cacheEntry) =>
			{
				cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);

				try
				{
					var latestVersionInformation = await _apiClient.GetLatestReleaseAsync();
					var newVersionAvailable = IsReleaseNewerThanInstalledVersion(latestVersionInformation.Tag_Name, Constants.AppVersion);

					AppMetrics.SyncUpdateAvailableMetric(newVersionAvailable, latestVersionInformation.Tag_Name);

					return new AmbientWeatherServerLatestRelease()
					{
						LatestVersion = latestVersionInformation.Tag_Name,
						ReleaseDate = latestVersionInformation.Published_At,
						ReleaseUrl = latestVersionInformation.Html_Url,
						Description = latestVersionInformation.Body,
						IsReleaseNewerThanInstalledVersion = newVersionAvailable
					};
				}
				catch (Exception e)
				{
					_logger.Error(e, "Error occurred while checking for new release.");
					cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
					return new AmbientWeatherServerLatestRelease();
				}
			});
		}
	}

	private static bool IsReleaseNewerThanInstalledVersion(string? releaseVersion, string? currentVersion)
	{
		if (string.IsNullOrEmpty(releaseVersion))
		{
			_logger.Verbose("Latest Release version from GitHub was null");
			return false;
		}

		if (string.IsNullOrEmpty(currentVersion))
		{
			_logger.Verbose("Current install version is null");
			return false;
		}

		var isCurrentVersionRC = currentVersion.Trim().ToLower().Contains("-rc");

		var cleanedReleaseVersion = releaseVersion.Trim().ToLower().Replace("v", string.Empty);
		var cleanedInstalledVersion = currentVersion.Trim().ToLower().Replace("-rc", string.Empty);

		if (!Version.TryParse(cleanedReleaseVersion, out var latestVersion))
		{
			_logger.Verbose("Failed to parse latest release version: {@Version}", cleanedReleaseVersion);
			return false;
		}

		if (!Version.TryParse(cleanedInstalledVersion, out var installedVersion))
		{
			_logger.Verbose("Failed to parse installed version: {@Version}", cleanedInstalledVersion);
			return false;
		}

		if (isCurrentVersionRC)
			return latestVersion >= installedVersion;

		return latestVersion > installedVersion;
	}
}
