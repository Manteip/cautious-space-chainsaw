using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

public class BlobUploader
{
    private readonly BlobContainerClient _container;

    public BlobUploader(BlobContainerClient container) => _container = container;

    public async Task UploadChunksAsync(string blobName, byte[][] chunks)
    {
        var blockBlob = _container.GetBlockBlobClient(blobName);

        foreach (var chunk in chunks)
        {
            using var ms = new MemoryStream(chunk);
            // missing ParallelTransferOptions; lots of small PUTs = higher costs/latency
            await blockBlob.StageBlockAsync(System.Convert.ToBase64String(System.Guid.NewGuid().ToByteArray()), ms);
        }

        
    }
}
