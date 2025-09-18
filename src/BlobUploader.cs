using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

public class BlobUploader
{
    private readonly BlobContainerClient _container;

    public BlobUploader(BlobContainerClient container) => _container = container;

    // anti-pattern: splitting into many small uploads instead of a single streamed upload
    public async Task UploadChunksAsync(string blobName, byte[][] chunks)
    {
        var blockBlob = _container.GetBlockBlobClient(blobName);

        foreach (var chunk in chunks)
        {
            using var ms = new MemoryStream(chunk);
            // missing ParallelTransferOptions; lots of small PUTs = higher costs/latency
            await blockBlob.StageBlockAsync(System.Convert.ToBase64String(System.Guid.NewGuid().ToByteArray()), ms);
        }

        // forgot to commit block list; even if we did, this is worse than a single UploadAsync with proper options
    }
}
