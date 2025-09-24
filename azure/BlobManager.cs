using Azure;
using Azure.Identity;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Azure.Storage.Sas;
using Endpoint.Flash.Core.Common.FlashException;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using PublicAccessType = Azure.Storage.Blobs.Models.PublicAccessType;

namespace Endpoint.Flash.Core.Extensions.StorageAccount;

public class BlobManager : IBlobManager
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly DataLakeServiceClient _dataLakeServiceClient;
    private DataLakeFileSystemClient dataLakeFileSystemClient;
    private readonly TimeSpan _leaseTimeout = TimeSpan.FromSeconds(60);  // Lease duration
    private readonly int _retryDelay = 1000;  // Delay between retries in milliseconds
    private readonly int _maxRetries = 60;  // Max number of retries to acquire the lease

    public BlobManager(BlobServiceClient blobServiceClient, DataLakeServiceClient dataLakeServiceClient = null!)
    {
        _blobServiceClient = blobServiceClient;
        _dataLakeServiceClient = dataLakeServiceClient;
    }

    public async Task<byte[]> GetBlobStreamAsync(string blobId, string blobContainerName)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var client = container.GetBlobClient(blobId);

        var exists = await client.ExistsAsync();
        return !exists.Value ? Array.Empty<byte>() : await GetBytes(client);
    }

    public async Task<(byte[] Content, string ContainerName, string Name)> GetBlobStreamAsync(string blobUri)
    {
        BlobClient client = new(new Uri(blobUri), new DefaultAzureCredential());
        return (
            Content: await GetBytes(client),
            ContainerName: client.BlobContainerName,
            Name: client.Name
            );
    }

    public async Task<IEnumerable<BlobItem>> GetBlobItemsAsync(string blobContainerName, string prefix)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);

        var blobTraits = new BlobTraits() { };
        var blobStates = new BlobStates() { };
        var results = new List<BlobItem>();
        await foreach (var blobItem in container.GetBlobsAsync(blobTraits, blobStates, prefix))
        {
            results.Add(blobItem);
        }

        return results;
    }

    public async Task<string> GetBlobAsync(string blobId, string blobContainerName)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var client = container.GetBlobClient(blobId);
        var exists = await client.ExistsAsync();
        return !exists.Value ? string.Empty : await GetString(client);
    }

    public async Task<(string Content, string ContainerName, string Name)> GetBlobAsync(string sasUri)
    {
        BlobClient client = new(new Uri(sasUri), null);
        return (
            Content: await GetString(client),
            ContainerName: client.BlobContainerName,
            Name: client.Name
            );
    }

    public async Task<T?> GetStreamAsync<T>(string blobId, string blobContainerName) where T : new()
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var client = container.GetBlobClient(blobId);
        return await GetObject<T>(client);
    }

    public async Task<(T? Content, string ContainerName, string Name)> GetStreamAsync<T>(string sasUri) where T : new()
    {
        BlobClient client = new(new Uri(sasUri), null);

        return (
          Content: await GetObject<T?>(client),
          ContainerName: client.BlobContainerName,
          Name: client.Name
          );
    }

    public Uri? GetServiceSasUriForBlob(Uri blobUri, TimeSpan validitySpan, string? storedPolicyName = null)
    {
        var blobClient = new BlockBlobClient(blobUri, new DefaultAzureCredential());

        // Check whether this BlobClient object has been authorized with Shared Key.
        if (blobClient.CanGenerateSasUri)
        {
            // Create a SAS token that's valid for TimeSpan.
            BlobSasBuilder sasBuilder = new()
            {
                BlobContainerName = blobClient.GetParentBlobContainerClient().Name,
                BlobName = blobClient.Name,
                Resource = "b"
            };

            if (storedPolicyName == null)
            {
                sasBuilder.ExpiresOn = DateTime.UtcNow.Add(validitySpan);
                sasBuilder.SetPermissions(BlobSasPermissions.Read);
            }
            else
            {
                sasBuilder.Identifier = storedPolicyName;
            }

            Uri? sasUri = blobClient.GenerateSasUri(sasBuilder);
            return sasUri;
        }
        else
        {
            return null;
        }
    }

    public Uri? GetServiceSasUriForBlob(
        string storageConnectionString,
        string containerName,
        string fileName,
        TimeSpan validitySpan,
        string? storedPolicyName = null)
    {
        var blobClient = new BlockBlobClient(storageConnectionString, containerName, fileName);
        // Check whether this BlobClient object has been authorized with Shared Key.
        if (blobClient.CanGenerateSasUri)
        {
            // Create a SAS token that's valid for TimeSpan.
            BlobSasBuilder sasBuilder = new()
            {
                BlobContainerName = blobClient.GetParentBlobContainerClient().Name,
                BlobName = blobClient.Name,
                Resource = "b"
            };

            if (storedPolicyName == null)
            {
                sasBuilder.ExpiresOn = DateTime.UtcNow.Add(validitySpan);
                sasBuilder.SetPermissions(BlobSasPermissions.Read);
            }
            else
            {
                sasBuilder.Identifier = storedPolicyName;
            }

            Uri? sasUri = blobClient.GenerateSasUri(sasBuilder);
            return sasUri;
        }
        else
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string blobId, string blobContainerName, CancellationToken cancellationToken = default)
    {
        Response<bool> response = null!;
        string leaseId = null!;

        var containerClient = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var client = containerClient.GetBlobClient(blobId);
        var blobLeaseClient = client.GetBlobLeaseClient();

        try
        {
            // don't try to delete until a lease can be aquired
            var blobLeaseResponse = await AcquireLeaseAsync(blobLeaseClient);
            leaseId = blobLeaseResponse.Value.LeaseId;
            var conditions = new BlobRequestConditions
            {
                LeaseId = leaseId,
            };
            response = await client.DeleteIfExistsAsync(conditions: conditions, cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            // Unable to delete the blob, will release lock in finally
        }
        finally
        {
            // Release the lease if it was unable to delete
            if (leaseId != null && !(response?.Value ?? true))
            {
                var leaseRequestConditions = new BlobRequestConditions { LeaseId = leaseId };
                await blobLeaseClient.ReleaseAsync(leaseRequestConditions, cancellationToken);
            }
        }

        return response?.Value ?? false;
    }


    public async Task<string?> UploadAsync(byte[] bytes, string blobContainerName, string blobName)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);

        var client = container.GetBlobClient(blobName);
        using (var stream = new MemoryStream(bytes))
        {
            _ = await client.UploadAsync(stream, new BlobHttpHeaders { ContentType = MimeTypeMap.GetMimeType(Path.GetExtension(blobName)) });
        }

        return client.Uri?.ToString();
    }

    public async Task<string?> UploadAsync<T>(T obj, string blobContainerName, string blobName) where T : new()
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);

        var client = container.GetBlobClient(blobName);
        using (var stream = new MemoryStream())
        {
            var json = JsonConvert.SerializeObject(obj);
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            writer.Flush();

            stream.Position = 0;
            _ = await client.UploadAsync(stream, new BlobHttpHeaders { ContentType = MimeTypeMap.GetMimeType(Path.GetExtension(blobName)) });
        }
        return client.Uri?.ToString();
    }

    public string? FileUriAsync(string blobId, string blobContainerName)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var client = container.GetBlobClient(blobId);
        return client.Uri?.ToString();
    }

    public async Task<bool> FileExistsAsync(string blobId, string blobContainerName)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var client = container.GetBlobClient(blobId);
        var doesExists = await client.ExistsAsync();
        return doesExists.Value;
    }

    public async Task<(bool Exists, string ContainerName, string Name)> FileExistsAsync(string sasUri)
    {
        BlobClient client = new(new Uri(sasUri), null);
        var fileExists = await client.ExistsAsync();
        return (
         Exists: fileExists.Value,
         ContainerName: client.BlobContainerName,
         Name: client.Name
         );

    }

    public async Task CreateContainerAsync(string blobContainerName)
    {
        var container = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        _ = await container.CreateIfNotExistsAsync(PublicAccessType.None);
    }

    public async Task AppendFileTextAsync(string obj, string blobContainerName, string blobName, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(obj + Constants.NewLine).ToArray();
        await AppendFileBytesAsync(bytes, blobContainerName, blobName, cancellationToken);
    }

    public async Task AppendFileBytesAsync(byte[] bytes, string blobContainerName, string blobName, CancellationToken cancellationToken = default)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(blobContainerName);
        var appendBlobClient = containerClient.GetAppendBlobClient(blobName);

        _ = await appendBlobClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        int retryCount = 0;
        while (retryCount < _maxRetries)
        {
            try
            {
                // Write the bytes to the append blob
                if (bytes.Length > 0)
                {
                    await AppendToBlob(bytes, appendBlobClient, cancellationToken);
                    break; // Exit the loop upon successful append
                }
            }
            catch (RequestFailedException e) when (
                    e.Status == (int)HttpStatusCode.PreconditionFailed ||
                    e.Status == (int)HttpStatusCode.NotFound ||
                    e.Status == (int)HttpStatusCode.Conflict
                    )
            {
                // If lease is already present, wait and retry
                retryCount++;
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }
    }

    public async Task<List<string>> GetAllFilesAsync(string folder, string blobContainer, int maxDegreeOfParallelism = 8)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(blobContainer);
        if (!folder.EndsWith("/")) folder += "/";

        // Step 1: List all blobs in the folder
        var blobNames = new List<string>();
        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: folder))
        {
            blobNames.Add(blobItem.Name);
        }

        // Step 2: Download all blobs in parllel
        ConcurrentBag<string> fileContents = await ReadBlobsInParallelAsync(maxDegreeOfParallelism, containerClient, blobNames);

        return [.. fileContents];

    }

    public async Task<bool> DeleteFolderAsync(string folder, string blobContainer, CancellationToken cancellationToken = default)
    {
        if (_dataLakeServiceClient is null)
            throw new FlashException("DataLakeServiceClient is not initialized. Cannot delete folder.");

        var fileSystemClient = _dataLakeServiceClient.GetFileSystemClient(blobContainer);
        var directoryClient = fileSystemClient.GetDirectoryClient(folder);
        var response = await directoryClient.DeleteAsync(recursive: true, cancellationToken: cancellationToken);
        return !response.IsError;
    }



    #region Private Methods


    private static async Task<ConcurrentBag<string>> ReadBlobsInParallelAsync(int maxDegreeOfParallelism, BlobContainerClient containerClient, List<string> blobNames)
    {
        var fileContents = new ConcurrentBag<string>(); // Thread-safe collection

        using (var semaphore = new SemaphoreSlim(maxDegreeOfParallelism))
        {
            var tasks = new List<Task>();

            foreach (var blobName in blobNames)
            {
                await semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var blobClient = containerClient.GetBlobClient(blobName);
                        using var ms = new MemoryStream();
                        await blobClient.DownloadToAsync(ms);
                        ms.Position = 0;

                        using var reader = new StreamReader(ms, Encoding.UTF8);
                        string content = await reader.ReadToEndAsync();
                        fileContents.Add(content);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        return fileContents;
    }


    private static async Task AppendToBlob(byte[] bytes, AppendBlobClient appendBlobClient, CancellationToken cancellationToken)
    {
        var blobProperties = await appendBlobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        var etag = blobProperties.Value.ETag;

        using MemoryStream stream = new(bytes);
        var blobUploadOptions = new AppendBlobAppendBlockOptions
        {
            Conditions = new AppendBlobRequestConditions
            {
                IfMatch = etag
            }
        };
        _ = await appendBlobClient.AppendBlockAsync(stream, blobUploadOptions, cancellationToken);
    }

    private async Task<Response<BlobLease>> AcquireLeaseAsync(BlobLeaseClient blobLeaseClient)
    {
        int retryCount = 0;
        while (retryCount < _maxRetries)
        {
            try
            {
                return await blobLeaseClient.AcquireAsync(_leaseTimeout);
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.LeaseAlreadyPresent)
            {
                // If lease is already present, wait and retry
                retryCount++;
                await Task.Delay(_retryDelay);
            }
        }

        throw new FlashException($"Unable to acquire lease after {_maxRetries} retries.");
    }

    private static async Task<byte[]> GetBytes(BlobClient client)
    {
        using var ms = new MemoryStream();
        _ = await client.DownloadToAsync(ms);
        ms.Position = 0;
        return ms.ToArray();
    }

    private static async Task<string> GetString(BlobClient client)
    {
        var download = await client.DownloadContentAsync();
        return Encoding.UTF8.GetString(download.Value.Content);
    }

    private static async Task<T?> GetObject<T>(BlobClient client) where T : new()
    {
        using var ms = new MemoryStream();
        _ = await client.DownloadToAsync(ms);
        ms.Position = 0;
        var serializer = new JsonSerializer();
        using var sr = new StreamReader(ms);
        using var jsonTextReader = new JsonTextReader(sr);
        return serializer.Deserialize<T>(jsonTextReader);
    }

    public async Task<IEnumerable<AdlsFileInfo>> GetBlobMatchingPatternAsync(string containerName, string directoryPath, IEnumerable<string> searchPatterns, int maxDegreeOfParallelism = 8)
    {
        if (dataLakeFileSystemClient is null)
        {
            dataLakeFileSystemClient = _dataLakeServiceClient.GetFileSystemClient(containerName);
        }

        // First, find all matching files
        var matchingPaths = new List<PathItem>();
        // Process pages in batches to reduce memory pressure
        await foreach (var page in dataLakeFileSystemClient.GetPathsAsync(directoryPath, recursive: true).AsPages(pageSizeHint: 1000))
        {
            // Process all items in the current page at once
            var pageMatches = page.Values
                .Where(pathItem => pathItem.IsDirectory.HasValue && !pathItem.IsDirectory.Value && searchPatterns.Contains(Path.GetFileName(pathItem.Name)))
                .ToHashSet();

            matchingPaths.AddRange(pageMatches);
        }

        // Then, get their content in parallel
        var matchingFiles = new ConcurrentBag<AdlsFileInfo>();
        using (var semaphore = new SemaphoreSlim(maxDegreeOfParallelism))
        {
            var tasks = matchingPaths.Select(async pathItem =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var fileClient = dataLakeFileSystemClient.GetFileClient(pathItem.Name);
                    var response = await fileClient.ReadAsync();

                    using var streamReader = new StreamReader(response.Value.Content);
                    var content = await streamReader.ReadToEndAsync();

                    matchingFiles.Add(new AdlsFileInfo
                    {
                        Path = pathItem.Name,
                        LastModified = pathItem.LastModified,
                        Content = content
                    });
                }
                catch (Exception)
                {
                    // Handle any exceptions during content retrieval
                    matchingFiles.Add(new AdlsFileInfo
                    {
                        Path = pathItem.Name,
                        LastModified = pathItem.LastModified,
                        Content = string.Empty
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        return matchingFiles;
    }




    #endregion
}// trigger review Wed Sep 24 13:45:24 UTC 2025
// trigger Wed Sep 24 13:51:26 UTC 2025
// trigger Wed Sep 24 13:59:52 UTC 2025
