using Endpoint.Flash.Core.Common;
using Endpoint.Flash.Core.Domain;
using Endpoint.Flash.Core.Domain.Context;
using Endpoint.Flash.Core.Domain.V2.Interface;

namespace Endpoint.Flash.Core.Extensions.Redis
{
    public static class RedisCacheKeyExtension
    {
        public static string GetCacheKey<T>(this StudySession studySession, Settings settings, string serviceName = null!, params string[] cacheKeyParts)
        {
            serviceName = GetServiceName(settings, serviceName);
            string modelKey = GetModelKey<T>(settings!);

            var cacheKeyPartsList = string.Join(":", cacheKeyParts.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(cacheKeyPartsList))
            {
                modelKey = $"{cacheKeyPartsList}:{modelKey}";
            }

            if (typeof(T).IsAssignableTo(typeof(IStudyRuntimeBaseEntity)))
            {
                return $"{{{studySession.StudyId}:{studySession.Environment}}}:{settings!.ReleaseVersion}:{modelKey}:{serviceName}";
            }
            else
            {
                return $"{{{studySession.StudyId}:{studySession.StudyVersion}}}:{settings!.ReleaseVersion}:{modelKey}:{serviceName}";
            }
        }

        public static string GetCacheKey<T>(Settings settings, string serviceName = null!, string hashTag = null!, params string[] cacheKeyParts)
        {
            serviceName = GetServiceName(settings, serviceName);
            var cacheKeyPartsList = string.Join(":", cacheKeyParts.Where(x => !string.IsNullOrWhiteSpace(x)));
            var cacheKeyPrefix = string.IsNullOrWhiteSpace(cacheKeyPartsList) ? string.Empty : $"{cacheKeyPartsList}";
            if (!string.IsNullOrWhiteSpace(hashTag))
            {
                cacheKeyPrefix = $"{{{hashTag}}}:{cacheKeyPrefix}";
            }
            return $"{cacheKeyPrefix}:{settings!.ReleaseVersion}:{GetModelKey<T>(settings!)}:{serviceName}";
        }

        public static string GetCotsCacheKey<T>(Settings settings, string serviceName = null!)
        {
            serviceName = GetServiceName(settings, serviceName);
            return $"{{{settings!.ReleaseVersion}:Cots}}:{GetModelKey<T>(settings!)}:{serviceName}";
        }

        private static string GetModelKey<T>(Settings settings)
        {
            var docType = typeof(T).GetFriendlyName();
            var modelKey = docType;
            if (settings!.CacheSettings is not null && settings.CacheSettings.CacheModelConfig.TryGetValue(docType, out string? configModels))
                modelKey = string.IsNullOrWhiteSpace(configModels) ? modelKey : configModels;
            return $"~{modelKey}~";
        }

        private static string GetServiceName(Settings settings, string serviceName)
        {
            serviceName ??= settings?.ServiceName ?? string.Empty;
            serviceName = serviceName.ToLower().Replace(" ", string.Empty);
            return serviceName;
        }
    }
}

//mkdqkmkdq