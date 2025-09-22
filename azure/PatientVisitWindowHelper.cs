using Azure.Messaging;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Endpoint.Flash.Core.Common;
using Azure.Storage.Blobs.Specialized;

namespace Endpoint.Flash.Patients.Runtime.Functions.Patient.Helpers
{
    public static class PatientVisitWindowHelper
    {
        public static string ToUnixTimeMilliseconds(this DateTime dateTime)
        {
            var dto = new DateTimeOffset(dateTime.ToUniversalTime());
            return dto.ToUnixTimeMilliseconds().ToString();
        }

        public static async Task SaveToBlob(BlobContainerClient outputContainer, dynamic data, string fullBlobPath)
        {
            //Prepare Object and Save to Blob
            await outputContainer.CreateIfNotExistsAsync();

            var cloudBlockBlob = outputContainer.GetBlobClient(fullBlobPath);
            var blobHttpHeader = new BlobHttpHeaders { ContentType = MediaTypeNames.Application.Json };

            await cloudBlockBlob.UploadAsync(BinaryData.FromString(data.ToString()),
               new BlobUploadOptions
               {
                   HttpHeaders = blobHttpHeader
               });
        }

        public static async Task AppendBlob(BlobContainerClient outputContainer, dynamic data, string fullBlobPath, bool addNewLine = true)
        {
            await outputContainer.CreateIfNotExistsAsync();

            var appendBlobClient = outputContainer.GetAppendBlobClient(fullBlobPath);

            await appendBlobClient.CreateIfNotExistsAsync();

            var dataString = JsonConvert.SerializeObject(data);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(addNewLine ? dataString + Environment.NewLine : dataString));
            await appendBlobClient.AppendBlockAsync(stream);
        }

        public static string GetPatientVisitWindowBlobPath(string sponsorId, string studyId, string environmentId, string entityType, string patienId, string visitId, int OccurrenceCnt)
        {
            sponsorId = string.IsNullOrWhiteSpace(sponsorId) ? Core.Common.Constants.UnKnown : sponsorId;
            studyId = string.IsNullOrWhiteSpace(studyId) ? Core.Common.Constants.UnKnown : studyId;
            environmentId = string.IsNullOrWhiteSpace(environmentId) ? Core.Common.Constants.UnKnown : environmentId;
            entityType = string.IsNullOrWhiteSpace(entityType) ? Core.Common.Constants.UnKnown : entityType;
            patienId = string.IsNullOrWhiteSpace(patienId) ? Core.Common.Constants.UnKnown : patienId;
            visitId = string.IsNullOrWhiteSpace(visitId) ? Core.Common.Constants.UnKnown : visitId;

            return $"sponsor={sponsorId}/study={studyId}/environment={environmentId}/{entityType}/patient={patienId}/VisitWindowData:{visitId}:{OccurrenceCnt}.jsonl";
        }
    }
}
