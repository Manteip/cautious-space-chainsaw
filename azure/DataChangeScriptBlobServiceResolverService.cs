using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Endpoint.Flash.Core.Extensions.StorageAccount;
using Endpoint.Flash.Core.Extensions.StorageAccount.Interface;

namespace Endpoint.Flash.SystemOperations.Runtime.Service;

public class DataChangeScriptBlobServiceResolverService : IDataChangeScriptBlobServiceResolverService
{
    public IBlobServiceAsync GetBlobService(ISettings settings)
    {
        var blobClient = new BlobServiceClient(new Uri(settings.DataChangeScriptOptions.StorageAccountConnectionUri), new DefaultAzureCredential(), BlobExtension.GetBlobOptions(settings));
        var blobManager = new BlobManager(blobClient);
        return new BlobServiceAsync(settings.DataChangeScriptOptions.StorageAccountConnectionUri, blobManager, settings.MaxDegreeOfParallelism);
    }
}

public class CustomDataChangeScriptBlobServiceResolverService : ICustomDataChangeScriptBlobServiceResolverService
{
    public IBlobServiceAsync GetBlobService(ApplicationSettings settings)
    {
        var blobClient = new BlobServiceClient(new Uri(settings.CustomActionBlobConnectionString!), new DefaultAzureCredential(), BlobExtension.GetBlobOptions(settings));
        var blobManager = new BlobManager(blobClient);
        return new BlobServiceAsync(settings.CustomActionBlobConnectionString!, blobManager, settings.MaxDegreeOfParallelism);
    }
}


public static class BlobExtension
{

    public static BlobClientOptions GetBlobOptions(ISettings settings)
    {
        BlobClientOptions options = new();
        options.Retry.Mode = RetryMode.Exponential;
        options.Retry.MaxRetries = settings.RetrySettings.MaxNumberOfAttempts;
        options.Retry.Delay = TimeSpan.FromSeconds(settings.RetrySettings.BackoffCoefficient);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(settings.RetrySettings.MaxRetryTimeOutIntervalInMilliseconds);
        return options;
    }
}