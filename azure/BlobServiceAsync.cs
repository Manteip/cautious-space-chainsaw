using Azure.ResourceManager.Storage;
using Azure.Storage.Blobs.Models;

namespace Endpoint.Flash.Core.Extensions.StorageAccount;

public class BlobServiceAsync : IBlobServiceAsync, IAuditBlobServiceAsync, IRestrictedAuditBlobServiceAsync, IDocGenBlobServiceAsync
{

    #region variables

    private readonly string _azureStorageConnectionString;
    private readonly IBlobManager _blobManager;
    private readonly int _maxDegreeOfParallelism;
    #endregion

    #region Constructor

    public BlobServiceAsync(string azureStorageConnectionString, IBlobManager blobManger, int maxDegreeOfParallelism)
    {
        if (string.IsNullOrWhiteSpace(azureStorageConnectionString))
        {
            throw new ArgumentNullException(nameof(azureStorageConnectionString));
        }

        _blobManager = blobManger;
        _azureStorageConnectionString = azureStorageConnectionString;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    #endregion

    #region Public Methods

    public async Task<IEnumerable<AdlsFileInfo>> GetBlobMatchingPatternAsync(string containerName, string directoryPath, IEnumerable<string> searchPatterns)
    {
        return await _blobManager.GetBlobMatchingPatternAsync(containerName, directoryPath, searchPatterns);
    }


    public async Task<FileMetadata> SaveFileAsync(string fileId, string blobContainerPath, string fileName, byte[] fileBytes, Dictionary<string, object>? fileProperties = null)
    {
        var blob = await _blobManager.UploadAsync(fileBytes, blobContainerPath, $"{fileId}");
        var metadata = GetFileMetadata(fileId, fileName, blob, fileProperties);
        return metadata;
    }

    public async Task<FileMetadata> SaveFileAsync(string fileId, string blobContainerPath, string fileName, string fileText, Dictionary<string, object>? fileProperties = null)
    {
        var blob = await _blobManager.UploadAsync(Encoding.UTF8.GetBytes(fileText), blobContainerPath, $"{fileId}");
        var metadata = GetFileMetadata(fileId, fileName, blob, fileProperties);
        return metadata;
    }

    public async Task<byte[]> GetFileBytesAsync(string fileId, string blobContainerPath)
    {
        return await _blobManager.GetBlobStreamAsync($"{fileId}", blobContainerPath);
    }

    public async Task<byte[]> GetFileBytesAsync(string uri)
    {
        var (content, _, _) = await _blobManager.GetBlobStreamAsync(uri);
        return content;
    }

    public async Task<IEnumerable<BlobItem>> GetBlobItemsAsync(string blobContainerPath, string prefix)
    {
        return await _blobManager.GetBlobItemsAsync(blobContainerPath, prefix);
    }

    public async Task<string> GetFileTextAsync(string fileId, string blobContainerPath)
    {
        return await _blobManager.GetBlobAsync($"{fileId}", blobContainerPath);

    }

    public async Task<string> GetFileTextAsync(string uri)
    {
        var (content, _, _) = await _blobManager.GetBlobAsync(uri);
        return content;
    }

    public Uri? GetFileAccessToken(string fileId, TimeSpan validitySpan, string blobContainerPath)
    {
        return _blobManager.GetServiceSasUriForBlob(_azureStorageConnectionString, blobContainerPath, $"{fileId}", validitySpan);
    }


    public async Task<bool> ExistsAsync(string fileId, string blobContainerPath)
    {
        return await _blobManager.FileExistsAsync($"{fileId}", blobContainerPath);
    }

    public async Task<bool> ExistsAsync(string uri)
    {
        var (exists, _, _) = await _blobManager.FileExistsAsync(uri);
        return exists;
    }

    public async Task<bool> DeleteAsync(string fileId, string blobContainerPath)
    {
        return await _blobManager.DeleteAsync(fileId, blobContainerPath);
    }

    public async Task<List<T>> ReadJsonFilesFromFolderAsync<T>(string folderPath, string blobContainerPath) where T : class
    {
        var results = new List<T>();

        // List all blobs in the folder
        var blobItems = await _blobManager.GetBlobItemsAsync(blobContainerPath, folderPath);

        foreach (var blobItem in blobItems)
        {
            // Read the content of each blob
            var fileContent = await GetFileTextAsync(blobItem.Name, blobContainerPath);

            if (!string.IsNullOrWhiteSpace(fileContent))
            {
                // Deserialize the JSON content into the specified type
                var deserializedObject = JsonConvert.DeserializeObject<T>(fileContent);
                if (deserializedObject != null)
                {
                    results.Add(deserializedObject);
                }
            }
        }

        return results;
    }

    public async Task AppendFileTextAsync(string fileText, string fileId, string blobContainerPath)
    {
        await _blobManager.AppendFileTextAsync(fileText, blobContainerPath, fileId);
    }

    public async Task AppendFileBytesAsync(byte[] fileBytes, string fileId, string blobContainerPath)
    {
        await _blobManager.AppendFileBytesAsync(fileBytes, blobContainerPath, fileId);
    }

    public async Task<List<string>> GetAllFilesAsync(string folder, string blobContainer)
    {
        return await _blobManager.GetAllFilesAsync(folder, blobContainer, _maxDegreeOfParallelism);
    }


    public async Task<bool> DeleteFolderAsync(string folder, string blobContainer, CancellationToken cancellationToken = default)
    {
        return await _blobManager.DeleteFolderAsync(folder, blobContainer, cancellationToken);
    }

    #endregion

    #region Helper Methods

    private FileMetadata GetFileMetadata(string fileId, string fileName, string? uriFileName, Dictionary<string, object>? fileProperties = null)
    {
        var fileMetadata = new FileMetadata
        {
            Id = fileId,
            Name = fileName,
            Properties = fileProperties,
            MimeType = MimeTypeMap.GetMimeType(GetFileExtension(fileName))
        };
        fileMetadata.Uri = uriFileName ?? GetFileFullPath(string.Empty, fileMetadata);
        return fileMetadata;
    }

    private static string GetFileExtension(string fileName)
    {
        return Path.GetExtension(fileName);
    }

    private static string GetFileFullPath(string fileLocationPath, FileMetadata metadata)
    {
        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        var fileExtension = string.Empty;
        if (metadata.Name != null)
        {
            fileExtension = Path.GetExtension(metadata.Name);
        }

        return Path.Combine(fileLocationPath, $"{metadata.Id}{fileExtension}");
    }






    #endregion
}
