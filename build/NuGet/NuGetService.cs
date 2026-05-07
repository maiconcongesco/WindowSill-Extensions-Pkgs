using NuGet.Common;
using NuGet.Models.NuGetIndex;
using NuGet.Models.NuGetRegistration;
using NuGet.Models.NuGetSearch;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace NuGet;

/// <summary>
/// Service for interacting with NuGet.org APIs
/// </summary>
internal class NuGetService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly AsyncLazy<string?> _searchServiceUrl;

    public NuGetService()
    {
        var handler = new HttpClientHandler()
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };

        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        _searchServiceUrl = new AsyncLazy<string?>(GetSearchServiceUrlAsync);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>
    /// Gets package metadata from NuGet.org
    /// </summary>
    public async Task<CatalogEntry?> GetPackageMetadataAsync(string packageId)
    {
        try
        {
            var packageFromSearch = await SearchPackagesAsync(packageId);
            if (packageFromSearch is not null)
            {
                if (string.IsNullOrWhiteSpace(packageFromSearch?.registration))
                {
                    Serilog.Log.Error("Package {PackageId} does not have a registration URL.", packageId);
                    return null;
                }

                IReadOnlyList<CatalogEntry> catalogEntries
                    = await GetRegistrationCatalogEntriesAsync(packageFromSearch.registration);

                CatalogEntry? latestItem
                    = catalogEntries.LastOrDefault(
                        entry => string.Equals(entry.version, packageFromSearch.version, StringComparison.OrdinalIgnoreCase))
                    ?? catalogEntries.LastOrDefault();

                if (latestItem is null)
                {
                    Serilog.Log.Error("No catalog entry found for package {PackageId} on NuGet.org.", packageId);
                    return null;
                }

                return latestItem;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Failed to get metadata for package {PackageId}: {Error}", packageId, ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Validates that a package has the required information to be considered an extension
    /// </summary>
    public bool ValidateExtensionPackage(CatalogEntry catalogEntry, string expectedTag)
    {
        System.Collections.Generic.List<string> tags = catalogEntry.tags ?? [];
        bool hasRequiredTags
            = tags.Any(
                tag => string.Equals(tag, expectedTag, StringComparison.OrdinalIgnoreCase));

        bool hasDependencyOnWindowSillApi
            = catalogEntry.dependencyGroups?.Any(
                group => group.dependencies?.Any(
                    dep => string.Equals(dep.packageId, "WindowSill.Api", StringComparison.OrdinalIgnoreCase)) ?? false) ?? false;

        bool hasDescription = !string.IsNullOrWhiteSpace(catalogEntry.description);
        bool hasPackageContent = !string.IsNullOrWhiteSpace(catalogEntry.packageContent);
        bool hasVersion = !string.IsNullOrWhiteSpace(catalogEntry.version);
        bool hasAuthors = !string.IsNullOrWhiteSpace(catalogEntry.authors);

        return hasRequiredTags
            && hasDependencyOnWindowSillApi
            && hasDescription
            && hasPackageContent
            && hasVersion
            && hasAuthors
            && catalogEntry.listed;
    }

    private async Task<IReadOnlyList<CatalogEntry>> GetRegistrationCatalogEntriesAsync(string registrationUrl)
    {
        NuGetRegistrationResponse? response
            = await _httpClient.GetFromJsonAsync<NuGetRegistrationResponse>(
                registrationUrl,
                _jsonOptions);

        if (response?.items is null)
        {
            return [];
        }

        List<CatalogEntry> catalogEntries = [];
        foreach (Item page in response.items)
        {
            IEnumerable<Item>? packageItems = page.items;
            if ((packageItems is null || !packageItems.Any()) && !string.IsNullOrWhiteSpace(page.id))
            {
                Item? pagedResponse = await _httpClient.GetFromJsonAsync<Item>(page.id, _jsonOptions);
                packageItems = pagedResponse?.items;
            }

            foreach (Item packageItem in packageItems ?? [])
            {
                if (packageItem.catalogEntry is not null)
                {
                    catalogEntries.Add(packageItem.catalogEntry);
                }
            }

            if (page.catalogEntry is not null)
            {
                catalogEntries.Add(page.catalogEntry);
            }
        }

        return catalogEntries;
    }

    private async Task<NuGetPackageFromSearchResponse?> SearchPackagesAsync(string packageId)
    {
        try
        {
            string? searchServiceUrl = await _searchServiceUrl;
            if (string.IsNullOrEmpty(searchServiceUrl))
            {
                throw new Exception("NuGet Search service URL is not available.");
            }

            string url = $"{searchServiceUrl}?q={Uri.EscapeDataString(packageId.ToLowerInvariant())}&prerelease=true&semVerLevel=2.0.0";
            var response = await _httpClient.GetFromJsonAsync<NuGetSearchResponse>(url, _jsonOptions);
            var package = response?.data?.FirstOrDefault(p => p.packageid is not null && p.packageid.Equals(packageId, StringComparison.OrdinalIgnoreCase));

            if (package is null)
            {
                throw new Exception($"We search for package id {packageId} on NuGet.org but could not find a package that match exactly this id. Please ensure the package id is correct and that your NuGet package is listed / searchable on NuGet.org.");
            }

            return package;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Failed to search for packages with package id {packageId}: {Error}", packageId, ex.Message);
        }
        return null;
    }

    private async Task<string?> GetSearchServiceUrlAsync()
    {
        try
        {
            string url = $"https://api.nuget.org/v3/index.json";
            NuGetIndexResponse? response = await _httpClient.GetFromJsonAsync<NuGetIndexResponse>(url, _jsonOptions);

            return response?.resources?.FirstOrDefault(r => r.type == "SearchQueryService")?.id;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Failed to get the NuGet search service API url: {Error}", ex.Message);
        }

        return string.Empty;
    }
}
