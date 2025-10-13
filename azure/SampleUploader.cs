using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;

public class SampleUploader
{
    private readonly BlobClient _client;

    public SampleUploader(BlobClient client)
    {
        _client = client;
    }

    // ❌ Anti-pattern: tiny chunks in a loop
    public async Task UploadChunksAsync(Stream input)
    {
        byte[] buffer = new byte[1024]; // very small buffer
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            // Each iteration does a separate upload -> high transaction cost
            using (var ms = new MemoryStream(buffer, 0, read))
            {
                await _client.UploadAsync(ms, overwrite: true);
            }
        }
    }
}
