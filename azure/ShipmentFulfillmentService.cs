using System.Text;
using Azure.Storage.Blobs.Specialized;
using Endpoint.Flash.Aggregator.Domain.Design;
using Endpoint.Flash.Core.AutoNumber.Interfaces;
using Endpoint.Flash.Core.Common.FlashException;
using Endpoint.Flash.Core.Common.Retry;
using Endpoint.Flash.Core.Extensions.CosmosDb.Generic.DatatRepository.Interface;
using Endpoint.Flash.GlobalData.Domain.Location.StudyDepot;
using Endpoint.Flash.Inventory.Domain.ExpiryLimit;
using Endpoint.Flash.Shipment.Functions.Services;
using Endpoint.Flash.StudySettings.Domain.ShipmentSetting;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Polly;
using static Endpoint.Flash.Core.Common.Constants;

namespace Endpoint.Flash.Shipment.Functions.ShipmentFulfillment.Services;

#nullable enable
public class ShipmentFulfillmentService : IShipmentFulfillmentService
{
    private readonly IDataRepositoryManager<LocationShipmentRequestFulfilledTracker> _locationShipmentRequestFulfilledTrackerRepoManager;
    private readonly IDataRepositoryManager<ShipmentRequestBundlingTimeModel> _shipmentRequestBundlingTimeRepositoryManager;
    private readonly IAzureSearchManager<ShipmentRequestSearchView> _shipmentRequestsAzureSearchManager;
    private readonly IDataRepositoryManager<ShipmentRequestModel> _shipmentRequestRepoManager;
    private readonly IDataRepositoryManager<ShipmentRequestViewModel> _shipmentRequestViewRepoManager;
    private readonly IAzureSearchManager<ShipmentRequestSearchView> _shipmentRequestSearchManager;
    private readonly IDataRepositoryManager<ShipmentModel> _shipmentRepoManager;
    private readonly IDataRepositoryManager<ShipmentContentsModel> _shipmentContentsRepoManager;
    private readonly ILocationLookupService _locationLookupService;
    private readonly IAggregatorLookupService _aggregatorLookupService;
    private readonly IInventoryService _inventoryService;
    private readonly IStateBlobServiceClient _blobServiceClient;
    private readonly IIdentityGenerator _identityGenerator;
    private readonly ApplicationSettings _appSettings;
    private readonly IMapper _mapper;
    private readonly IMediator _mediator;
    private readonly ILogger<ShipmentFulfillmentService> _logger;
    private readonly IShipmentService _shipmentService;
    private readonly IShipmentRequestService _shipmentRequestService;

#pragma warning disable S107
    public ShipmentFulfillmentService(
        IDataRepositoryManager<LocationShipmentRequestFulfilledTracker> locationShipmentRequestFulfilledTrackerRepoManager,
        IDataRepositoryManager<ShipmentRequestBundlingTimeModel> shipmentRequestBundlingTimeRepositoryManager,
        IAzureSearchManager<ShipmentRequestSearchView> shipmentRequestsAzureSearchManager,
        IDataRepositoryManager<ShipmentRequestModel> shipmentRequestRepoManager,
        IDataRepositoryManager<ShipmentRequestViewModel> shipmentRequestViewRepoManager,
        IAzureSearchManager<ShipmentRequestSearchView> shipmentRequestSearchManager,
        IDataRepositoryManager<ShipmentModel> shipmentRepoManager,
        IDataRepositoryManager<ShipmentContentsModel> shipmentContentsRepoManager,
        ILocationLookupService locationLookupService,
        IAggregatorLookupService aggregatorLookupService,
        IInventoryService inventoryService,
        IStateBlobServiceClient blobServiceClient,
        IIdentityGenerator identityGenerator,
        ApplicationSettings appSettings,
        IMapper mapper,
        IMediator mediator,
        ILogger<ShipmentFulfillmentService> logger,
        IShipmentService shipmentService,
        IShipmentRequestService shipmentRequestService)
    {
#pragma warning restore S107
        _locationShipmentRequestFulfilledTrackerRepoManager = locationShipmentRequestFulfilledTrackerRepoManager;
        _shipmentRequestBundlingTimeRepositoryManager = shipmentRequestBundlingTimeRepositoryManager;
        _shipmentRequestsAzureSearchManager = shipmentRequestsAzureSearchManager;
        _shipmentRequestRepoManager = shipmentRequestRepoManager;
        _shipmentRequestViewRepoManager = shipmentRequestViewRepoManager;
        _shipmentRequestSearchManager = shipmentRequestSearchManager;
        _shipmentRepoManager = shipmentRepoManager;
        _shipmentContentsRepoManager = shipmentContentsRepoManager;
        _locationLookupService = locationLookupService;
        _aggregatorLookupService = aggregatorLookupService;
        _inventoryService = inventoryService;
        _blobServiceClient = blobServiceClient;
        _identityGenerator = identityGenerator;
        _appSettings = appSettings;
        _mapper = mapper;
        _mediator = mediator;
        _logger = logger;
        _shipmentService = shipmentService;
        _shipmentRequestService = shipmentRequestService;
    }

    public async Task<(List<(string destinationId, List<string> shipmentRequestIds)> shipmentRequests, bool readyForBundling)> GetShipmentRequestsForFulfillmentAsync(ShipmentFulfillmentTriggerRequest triggerRequest, FlashApplicationContext appContext, string eventId, Dictionary<string, string> customDimensions)
    {
        _logger.LogInformation("Running GetShipmentRequestsForFulfillmentAsync for {SponsorId} {StudyId} {StudyVersionId}", appContext.StudySession!.SponsorId, appContext.StudySession.StudyId, appContext.StudySession.StudyVersion);

        // this method will create records per study if they don't exist, using default values from COTS data
        var now = DateTime.UtcNow;
        var studyShipmentSettings = await _shipmentService.GetStudyShipmentSettings(appContext);
        var bundleTimeRepo = await _shipmentRequestBundlingTimeRepositoryManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!, eventId);
        Expression<Func<ShipmentRequestBundlingTimeModel, bool>> bundleTimesFilterExpression = d => d.IsActive;
        var bundletimeResult = await bundleTimeRepo.GetAsync(bundleTimesFilterExpression, appContext.StudySession.GetPartition<ShipmentRequestBundlingTimeModel>());
        var lastBundleTime = bundletimeResult.FirstOrDefault()?.BundleTimeLastRun;
        var scheduledTime = CrontabSchedule.Parse(studyShipmentSettings.BundleTime).GetNextOccurrence(now).TimeOfDay;

        var readyForBundling = false;
        if (lastBundleTime != null)
        {
            var nextOccurrence = CrontabSchedule.Parse(studyShipmentSettings.BundleTime).GetNextOccurrence(lastBundleTime.Value.UtcDateTime);
            readyForBundling = nextOccurrence <= now;
        }
        else
        {
            await bundleTimeRepo.UpdateAsync(new ShipmentRequestBundlingTimeModel
                {
                    SponsorId = appContext.StudySession.SponsorId!,
                    StudyId = appContext.StudySession.StudyId!,
                    EnvironmentId = appContext.StudySession.Environment!,
                    StudyVersionId = appContext.StudySession.StudyVersion!,
                    BundleTimeLastRun = new DateTimeOffset(now.Year, now.Month, now.Day, scheduledTime.Hours, scheduledTime.Minutes, scheduledTime.Seconds , TimeSpan.Zero).AddSeconds(-1),
                }, eventId, ignoreEtag: true, userSession: appContext.CurrentUser, customDimensions: customDimensions);
        }

        Expression<Func<ShipmentRequestSearchView, bool>> filterExpression =
            d => d.SponsorId == appContext.StudySession!.SponsorId &&
                d.EnvironmentId == appContext.StudySession!.Environment &&
                d.StudyId == appContext.StudySession!.StudyId &&
                d.IsFulfillmentProcessing != true;
        var filterQuery = QueryExtension.CreateQuery(filterExpression);
        // since QueryExtension.CreateQuery doesn't translate DateTime.MinValue correctly, needs to be built manually
        filterQuery[OpenApi.Name.Filter] = $"{filterQuery[OpenApi.Name.Filter]} and ({nameof(ShipmentRequestSearchView.Status).ToCamelCase()} eq '{ShipmentRequestStatus.Created}' or {nameof(ShipmentRequestSearchView.Status).ToCamelCase()} eq '{ShipmentRequestStatus.Approved}' or ({nameof(ShipmentRequestSearchView.FulfillmentReprocessingStartDate).ToCamelCase()} ne null and {nameof(ShipmentRequestSearchView.FulfillmentReprocessingStartDate).ToCamelCase()} ne {DateTime.MinValue:yyyy-MM-ddTHH:mm:ssZ}))";

        // if shipmentRequestIds are passed, process them regardless of type or bundling settings
        if (triggerRequest.ShipmentRequestIds?.Any() ?? false)
        {
            var shipmentRequestIdsFilter = new KeyValuePair<string, string>(Constants.OpenApi.Name.Filter, $"search.in({nameof(ShipmentRequestSearchView.Id).ToCamelCase()}, '{string.Join(",", triggerRequest.ShipmentRequestIds)}', ',')");
            filterQuery.AddQueryParametersForAzureSearch(shipmentRequestIdsFilter);
        }
        else
        {
            // initial and resupply will always be accounted for in this routine.
            // Manual or Requested types will be accounted for here when bundled, otherwise will have automatically had fulfillment routine fired with the specific shipment request id at time of creation/approval
            var autoRequestTypes = studyShipmentSettings.ShipmentRequestSettings.Where(x => x.ShipmentRequestType == ShipmentRequestType.Resupply.ToString() || x.ShipmentRequestType == ShipmentRequestType.Initial.ToString())
                .Select(x => x.ShipmentRequestType?.ToString()).ToList();
            var shipmentTypeFilter = new KeyValuePair<string, string>(Constants.OpenApi.Name.Filter, $"(search.in({nameof(ShipmentRequestSearchView.RequestType).ToCamelCase()}, '{string.Join(",", autoRequestTypes)}', ',') or {nameof(ShipmentRequestSearchView.IsBundled).ToCamelCase()} eq true or {nameof(ShipmentRequestSearchView.IsBundled).ToCamelCase()} eq null or ({nameof(ShipmentRequestSearchView.FulfillmentReprocessingStartDate).ToCamelCase()} ne null and {nameof(ShipmentRequestSearchView.FulfillmentReprocessingStartDate).ToCamelCase()} ne {DateTime.MinValue:yyyy-MM-ddTHH:mm:ssZ}))");
            filterQuery.AddQueryParametersForAzureSearch(shipmentTypeFilter);
        }
        var shipmentRequestsAzureSearch = await _shipmentRequestsAzureSearchManager.GetAzureSearchClient(appContext.StudySession.SponsorId!, appContext.StudySession.Environment!, eventId);

        // anytime shipmentRequestIds are passed to the subscriber we expect them to be found
        // so retry to account for the search index not returning them yet
        Func<List<ShipmentRequestSearchView>, bool> shipmentRequestQueryRetryCondition =
            x => (triggerRequest.ShipmentRequestIds?.Any() ?? false) && (triggerRequest.ShipmentRequestIds?.Count ?? 0) != x.Count;
        var shipmentRequestsRetryPolicy = CustomResultRetryPolicy.GetRetryPolicy(_appSettings, _logger, shipmentRequestQueryRetryCondition);
        var shipmentRequests = await shipmentRequestsRetryPolicy.ExecuteAsync(() => shipmentRequestsAzureSearch.GetAllPagesAsync(filterQuery));
        _logger.LogInformation("Found {shipmentRequestCount} shipment requests for fulfillment", shipmentRequests.Count);

        return (shipmentRequests
            .Join(studyShipmentSettings.ShipmentRequestSettings,
                sr => sr.RequestType,
                srs => srs.ShipmentRequestType?.ToString(),
                (sr, srs) => new { sr, srs })
            .Where(x => ( // those that tried to process but failed and need to be re-processed
                        x.sr.FulfillmentReprocessingStartDate.HasValue &&
                        x.sr.FulfillmentReprocessingStartDate <= now &&
                        x.sr.FulfillmentReprocessingStartDate.Value.AddHours(24) > now) ||
                        (
                        (!(x.srs.RequireApproval ?? false) || x.sr.Status == ShipmentRequestStatus.Approved.ToString()) && // those that don't require approval or are approved
                        (!(x.sr.IsBundled ?? x.srs.IsBundled ?? false) || // those that aren't bundled
                        ((x.sr.IsBundled ?? x.srs.IsBundled ?? false) && readyForBundling)) // those that are bundled and are ready to be bundled
                        )
            )
            .GroupBy(grp => grp.sr.DestinationId)
            .Select(x => (x.Key, x.Select(z => z.sr.Id).ToList())).ToList(), readyForBundling);
    }

    public async Task<ExpiryLimitView> GetExpiryLimits(string sponsorId, string studyId, string studyVersionId)
    {
        FlashApplicationContext appContext = new() { StudySession = new() { SponsorId = sponsorId, StudyId = studyId, StudyVersion = studyVersionId } };
        var studyDesignAgDomain = new StudyDesignDomain();
        await Aggregator.Service.Helper.StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.ExpiryLimits }, studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.ExpiryLimits;
    }

    public async Task<List<ShipmentModel>> CreateShipmentsAsync(List<ShipmentRequestContentsForFulfillment> shipmentRequestContents, string eventId, FlashApplicationContext appContext, Dictionary<string, string> customDimensions)
    {
        var sponsorId = appContext.StudySession!.SponsorId!;
        var kitTypeIds = shipmentRequestContents.Select(src => src.KitTypeId).Distinct();
        _logger.LogInformation("Creating shipments for {sponsorId} {studyId}", sponsorId, appContext.StudySession.StudyId);

        var inventoryForShipments = new List<(ShipmentRequestContentForFulfillment requestContent, string sourceId, string destinationId, string? bundleGroupId, List<ShipmentRequestModel> shipmentRequests, InventoryTrackerModel inventory)>();
        var partialShipmentRequestIds = new List<(string requestId, string destinationLocationId)>();
        var failedShipmentRequestIds = new List<(string requestId, string destinationLocationId)>();
        var completedShipmentRequestIds = new List<(string requestId, string destinationLocationId)>();
        var kitTypesTask = _aggregatorLookupService.GetKitTypes(sponsorId, appContext.StudySession!.StudyId!, appContext.StudySession!.StudyVersion!);
        var countryDepotPrioritiesTask = _aggregatorLookupService.GetCountryDepotPriorities(
                sponsorId, appContext.StudySession?.StudyId!,
                appContext.StudySession?.StudyVersion!);
        await Task.WhenAll(kitTypesTask, countryDepotPrioritiesTask);
        var kitTypes = await kitTypesTask;

        if (kitTypeIds.Any(x => !kitTypes.Any(y => y.Id == x))) throw new FlashException($"Invalid Kit Type Ids in Shipment request: {string.Join(", ", kitTypeIds)}; SponsorId: {appContext.StudySession!.SponsorId!}");

        var countryDepotPriorities = await countryDepotPrioritiesTask;

        var shipmentModels = new List<ShipmentModel>();

        // get all inventory and update shipments before processing next fulfillment for this sponsor
        try
        {
            foreach (var shipmentGroup in shipmentRequestContents.GroupBy(grp => new { grp.DestinationId, grp.BundleGroupId }).ToList())
            {
                inventoryForShipments = await BuildShipmentRequestContents(shipmentGroup.Key.DestinationId, shipmentGroup.Key.BundleGroupId, appContext, countryDepotPriorities, kitTypes, shipmentRequestContents, inventoryForShipments, partialShipmentRequestIds, failedShipmentRequestIds, completedShipmentRequestIds);
            }
            shipmentModels = await AssignInventoryToShipments(inventoryForShipments, eventId, appContext, customDimensions);
        }
        finally
        {
            await UpdateShipmentRequestStatusesAsync(failedShipmentRequestIds, partialShipmentRequestIds, completedShipmentRequestIds, eventId, appContext, customDimensions, kitTypes);
        }

        return shipmentModels;
    }

    public async Task SetShipmentRequestsProcessingStatus(List<string> shipmentRequestIds, FlashApplicationContext appContext, Dictionary<string, string> customDimensions, string eventId, bool isProcessing, bool setReprocessingStartDate = false, DateTimeOffset? reprocessingStartDate = null, ShipmentRequestStatus? status = null)
    {
        var shipmentRequestRepo = await _shipmentRequestRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!, eventId);
        var shipmentRequestViewModelRepo = await _shipmentRequestViewRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!, eventId);
        var shipmentRequestSearchClient = await _shipmentRequestSearchManager.GetAzureSearchClient(appContext.StudySession!.SponsorId!, appContext.StudySession!.Environment!, eventId);

        var patchOperations = new List<JsonDiffPatchDotNet.Formatters.JsonPatch.Operation>
        {
            new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation()
            {
                Op = JsonDiffPatchDotNet.Formatters.JsonPatch.OperationTypes.Replace,
                Path = $"/{nameof(ShipmentRequestModel.IsFulfillmentProcessing)}",
                Value = isProcessing
            }
        };

        if (setReprocessingStartDate)
        {
            patchOperations.Add(new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation()
            {
                Op = JsonDiffPatchDotNet.Formatters.JsonPatch.OperationTypes.Replace,
                Path = $"/{nameof(ShipmentRequestModel.FulfillmentReprocessingStartDate)}",
                Value = reprocessingStartDate
            });
        }
        if (status != null)
        {
            patchOperations.Add(new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation()
            {
                Op = JsonDiffPatchDotNet.Formatters.JsonPatch.OperationTypes.Replace,
                Path = $"/{nameof(ShipmentRequestModel.Status)}",
                Value = status.ToString()
            });
        }
        var shipmentRequestTasks = shipmentRequestIds.Select(srid =>
        {
            var patchTask = async () =>
            {
                _ = await shipmentRequestRepo.PatchAsync(srid,
                    patchOperations.PatchToCosmosPatchOperation(_appSettings.CosmosInfo!.ComsosSystemKeys),
                    appContext.StudySession!.GetPartition<ShipmentRequestModel>(),
                    eventId, appContext.CurrentUser!, customDimensions);

                var shipmentRequestViewModel = await shipmentRequestViewModelRepo.PatchAsync(srid,
                    patchOperations.PatchToCosmosPatchOperation(_appSettings.CosmosInfo!.ComsosSystemKeys),
                    appContext.StudySession!.GetPartition<ShipmentRequestViewModel>(),
                    eventId, appContext.CurrentUser!, customDimensions);
                var searchView = _mapper.Map<ShipmentRequestSearchView>(shipmentRequestViewModel);
                await shipmentRequestSearchClient.MergeOrUploadAsync(new List<ShipmentRequestSearchView> { searchView }, eventId, customDimensions);
            };
            return patchTask();
        });
        await Task.WhenAll(shipmentRequestTasks);
    }

    public async Task<List<ShipmentRequestContentsForFulfillment>> GetShipmentRequestContentsAsync(List<string> shipmentRequestIds,
        FlashApplicationContext appContext, Dictionary<string, string> customDimensions, string eventId)
    {
        var shipmentRequestRepo = await _shipmentRequestRepoManager.GetTenantDataRepositoryAsync(appContext!.StudySession!.SponsorId!, appContext!.StudySession!.Environment!);
        Expression<Func<ShipmentRequestModel, bool>> shipmentRequestFilter = x => x.IsActive &&
            shipmentRequestIds.Contains(x.Id);
        var studyShipmentSettings = await _shipmentService.GetStudyShipmentSettings(appContext);
        var shipmentRequests = await shipmentRequestRepo.GetAsync(shipmentRequestFilter, appContext!.StudySession.GetPartition<ShipmentRequestModel>());
        var shipmentRequestContents = shipmentRequests
            .Join(studyShipmentSettings.ShipmentRequestSettings,
                sr => sr.RequestType,
                sss => sss.ShipmentRequestType?.ToString(),
            (sr, sss) => new { sr, shipmentRequestSettings = sss })
            .SelectMany(x => x.sr.KitTypeContents, (x, src) => new { x.shipmentRequestSettings, x.sr, src })
            .Where(x => !(x.shipmentRequestSettings.RequireApproval ?? false) || x.src.Status == ShipmentRequestStatus.Approved.ToString())
            .GroupBy(grp => new
            {
                grp.sr.DestinationId,
                grp.src.KitTypeId,
                grp.shipmentRequestSettings,
                BundleGroupId = (grp.sr.IsBundled ?? grp.shipmentRequestSettings.IsBundled ?? false) ? null : grp.sr.Id,
                SourceId = string.IsNullOrWhiteSpace(grp.sr.SourceId) ? null : grp.sr.SourceId
            })
            .Select(x => new ShipmentRequestContentsForFulfillment
            {
                DestinationId = x.Key.DestinationId,
                BundleGroupId = x.Key.BundleGroupId, // bundled requests will all go into the same shipment. non bundled will create a shipment per request (and shipmentgroup/treatAsAmbient, further down)
                KitTypeId = x.Key.KitTypeId,
                ShipmentRequestSettings = x.Key.shipmentRequestSettings,
                ShipmentRequests = x.Select(y => y.sr).ToList(),
                SourceId = x.Key.SourceId,
                Amounts = x.GroupBy(grp => new
                {
                    grp.src.BatchNumber,
                    grp.src.LotNumber,
                    grp.src.VendorLotNumber,
                    grp.src.MfgLotNumber,
                    grp.src.ExpiryDate,
                    grp.src.SeqStartRange,
                    grp.src.SeqEndRange
                }).Select(y => new ShipmentRequestContentForFulfillment
                {
                    BatchNumber = y.Key.BatchNumber,
                    LotNumber = y.Key.LotNumber,
                    VendorLotNumber = y.Key.VendorLotNumber,
                    MfgLotNumber = y.Key.MfgLotNumber,
                    ExpiryDate = y.Key.ExpiryDate,
                    SeqStartRange = y.Key.SeqStartRange,
                    SeqEndRange = y.Key.SeqEndRange,
                    RequestedQuantity = int.Parse(Math.Min(int.MaxValue, y.Sum(y => (long?)y.src.ApprovedQuantity ?? y.src.RequestedQuantity)).ToString())
                }).ToList()
            }).ToList();
        return shipmentRequestContents;
    }

    public async Task LogState(List<ShipmentRequestContentsForFulfillment> shipmentRequestContents, FlashApplicationContext appContext)
    {
        if (string.IsNullOrWhiteSpace(_appSettings.StateLogStorageConnectionString)) return;
        var stateLogs = await GetStateLogs(shipmentRequestContents, appContext);
        var container = _blobServiceClient.GetBlobContainerClient(_appSettings.FulfillmentStateBlob);
        await container.CreateIfNotExistsAsync();
        // get the append blob client
        var appendBlobClient = container.GetAppendBlobClient($"sponsor={appContext.StudySession!.SponsorId}/study={appContext.StudySession.StudyId}/environment={appContext.StudySession!.Environment}/fulfillmentState.jsonl");
        await appendBlobClient.CreateIfNotExistsAsync();

        var bytes = stateLogs.SelectMany(inventoryState =>
        {
            // Serialize the inventoryState object to JSON
            string json = JsonConvert.SerializeObject(inventoryState, SerializerSettings.GetJsonSerializerSettings());

            // Convert the JSON string to bytes
            return Encoding.UTF8.GetBytes(json + Environment.NewLine);

        }).ToArray();
        // Write the bytes to the append blob
        if (bytes.Length > 0)
        {
            using (MemoryStream stream = new MemoryStream(bytes))
            {
                await appendBlobClient.AppendBlockAsync(stream);
            }
        }
    }

    public async Task<List<string>> GetPotentialSourceDepotsForShipmentRequests(
        string destinationId,
        List<ShipmentRequestContentsForFulfillment> shipmentRequestContents,
        FlashApplicationContext appContext)
    {
        var destinationLocation = await _locationLookupService.LookupLocationOrDepotByIdAsync(destinationId, appContext);
        var destinationDepot = destinationLocation == null ? await _locationLookupService.LookupLocationOrDepotByIdAsync(destinationId, appContext) : null;
        var distributionDestinationType = destinationDepot != null ? DistributionDestinationType.Depot : DistributionDestinationType.Country;
        var depotDistributionDestination = destinationDepot?.Id ?? destinationLocation?.CountryCode;
        if (string.IsNullOrWhiteSpace(depotDistributionDestination)) throw new FlashException($"Unable to determine distribution destination in {nameof(GetPotentialSourceDepotsForShipmentRequests)}");
        var depotDistributionGroups = await _aggregatorLookupService.GetDepotDistributionGroupsForDestinationType(appContext.StudySession!.SponsorId!, appContext.StudySession!.StudyId!, appContext.StudySession!.StudyVersion!, distributionDestinationType);

        return (await Task.WhenAll(shipmentRequestContents
                .Where(x => x.DestinationId == destinationId)
                .GroupBy(grp => new { grp.KitTypeId, grp.SourceId, grp.DestinationId })
                .Select(async rc =>
            {
                if (!string.IsNullOrWhiteSpace(rc.Key.SourceId)) return new List<string>() { rc.Key.SourceId }; // sourceId was passed for manual shipment so don't need to determine it
                return await GetSourceDepotIds(depotDistributionGroups, destinationLocation?.CountryCode, distributionDestinationType, depotDistributionDestination, rc.Key.KitTypeId, appContext.StudySession!.SponsorId!, appContext.StudySession.StudyId!, appContext.StudySession.StudyVersion!);
            }))).SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
    }

    #region private methods

#pragma warning disable S107 // Methods should not have too many parameters
    private async Task<List<(ShipmentRequestContentForFulfillment requestContent, string sourceId, string destinationId, string? bundleGroupId, List<ShipmentRequestModel> shipmentRequests, InventoryTrackerModel inventory)>>
        BuildShipmentRequestContents(string destinationId, string? bundleGroupId, FlashApplicationContext appContext,
            CountryDepotPriorityView countryDepotPriorities, List<KitTypeResponse> kitTypes, List<ShipmentRequestContentsForFulfillment> shipmentRequestContents,
            List<(ShipmentRequestContentForFulfillment requestContent, string sourceId, string destinationId, string? bundleGroupId, List<ShipmentRequestModel> shipmentRequests, InventoryTrackerModel inventory)> inventoryForShipments,
            List<(string requestId, string destinationLocationId)> partialShipmentRequestIds, List<(string requestId, string destinationLocationId)> failedShipmentRequestIds, List<(string requestId, string destinationLocationId)> completedShipmentRequestIds)
    {
#pragma warning restore S107 // Methods should not have too many parameters
        var destinationLocation = await _locationLookupService.LookupLocationOrDepotByIdAsync(destinationId, appContext);
        var destinationDepot = destinationLocation == null ? await _locationLookupService.LookupLocationOrDepotByIdAsync(destinationId, appContext) : null;
        var destinationType = destinationDepot != null ? LocationType.Depot : LocationType.Site;
        var distributionDestinationType = destinationDepot != null ? DistributionDestinationType.Depot : DistributionDestinationType.Country;
        var depotDistributionDestination = destinationDepot?.Id ?? destinationLocation?.CountryCode;
        if (string.IsNullOrWhiteSpace(depotDistributionDestination)) throw new FlashException("Unable to determine distribution destination");
        var depotDistributionGroups = await _aggregatorLookupService.GetDepotDistributionGroupsForDestinationType(appContext.StudySession!.SponsorId!, appContext.StudySession!.StudyId!, appContext.StudySession!.StudyVersion!, distributionDestinationType);
        var bundledRequestContents = shipmentRequestContents.Where(x => x.BundleGroupId == bundleGroupId && x.DestinationId == destinationId).ToList();
        foreach (var requestContent in bundledRequestContents.SelectMany(
            x => x.Amounts ?? [],
            (x, amt) => new { Amount = amt, x }).ToList())
        {

            if (!(requestContent.x.ShipmentRequestSettings.AllowPartial ?? false) && requestContent.x.ShipmentRequests.Exists(i => failedShipmentRequestIds.Exists(z => z.requestId == i.Id)) )
            {
                continue;
            }
            var usedInventory = inventoryForShipments
                .GroupBy(grp => new
                    {
                        grp.inventory.KitType,
                        grp.requestContent.LotNumber,
                        grp.requestContent.BatchNumber,
                        grp.requestContent.VendorLotNumber,
                        grp.requestContent.MfgLotNumber,
                        grp.requestContent.ExpiryDate,
                        grp.requestContent.SeqStartRange,
                        grp.requestContent.SeqEndRange
                    })
                .Where(x => x.Key.KitType == requestContent.x.KitTypeId &&
                    x.Key.LotNumber == requestContent.Amount.LotNumber &&
                    x.Key.BatchNumber == requestContent.Amount.BatchNumber &&
                    x.Key.VendorLotNumber == requestContent.Amount.VendorLotNumber &&
                    x.Key.MfgLotNumber == requestContent.Amount.MfgLotNumber &&
                    x.Key.ExpiryDate == requestContent.Amount.ExpiryDate &&
                    x.Key.SeqStartRange == requestContent.Amount.SeqStartRange &&
                    x.Key.SeqEndRange == requestContent.Amount.SeqEndRange)
                .SelectMany(x => GetExpectedRemainingQuantity(x.Select(y => (y.requestContent.FulfilledQuantity, y.inventory)).ToList())).ToList();
            var inventory = await GetInventoryForShipmentRequestContents(bundledRequestContents, destinationId,
                destinationType,
                countryDepotPriorities,
                depotDistributionGroups,
                distributionDestinationType, depotDistributionDestination,
                kitTypes,
                requestContent.x,
                destinationLocation?.CountryCode,
                requestContent.x.ShipmentRequestSettings,
                usedInventory, // any inventory already retrieved for this shipment that has quantity consumed will be excluded from the results
                appContext, requestContent.x.SourceId);
            inventoryForShipments = ClassifyShipmentRequestStatuses(inventory, inventoryForShipments, partialShipmentRequestIds, failedShipmentRequestIds, completedShipmentRequestIds, requestContent.x);
        }
        return inventoryForShipments;
    }
    private static List<(int RemaingingQuantity, InventoryTrackerModel Inv)> GetExpectedRemainingQuantity(List<(long fulfilledQuantity, InventoryTrackerModel inv)> inventoryforFulfillment)
    {
        var remainingInventory = new List<(int RemaingingQuantity, InventoryTrackerModel Inv)>();
        if (!(inventoryforFulfillment?.Any() ?? false))
        {
            return remainingInventory;
        }
        var fulfilledQuantity = inventoryforFulfillment[0].fulfilledQuantity; // Assuming each record per kit type will have the same fulfilledQuantity and this method will take records of only one kit type
        foreach (var inv in inventoryforFulfillment)
        {
            // serialized inventory is always fully consumed
            if (inv.inv.KitManagementType == (int)KitManagementType.Numbered)
            {
                remainingInventory.Add((0, inv.inv));
                continue;
            }

            if (fulfilledQuantity <= 0)
            {
                remainingInventory.Add((inv.inv.Quantity, inv.inv));
                continue;
            }

            int remaining = inv.inv.Quantity - int.Parse(Math.Min(int.MaxValue, fulfilledQuantity).ToString());

            if (remaining > 0)
            {
                remainingInventory.Add((remaining, inv.inv));
                fulfilledQuantity = 0;
            }
            else
            {
                fulfilledQuantity -= inv.inv.Quantity;
            }

        }
        return remainingInventory;
    }

    private async Task<List<ShipmentFulfillmentState>> GetStateLogs(List<ShipmentRequestContentsForFulfillment> shipmentRequestContents, FlashApplicationContext appContext)
    {
        var stateLogs = new List<ShipmentFulfillmentState>();

        var kitTypes = await _aggregatorLookupService.GetKitTypes(appContext.StudySession!.SponsorId!, appContext.StudySession!.StudyId!, appContext.StudySession!.StudyVersion!);
        var depotDistributionGroups = await _aggregatorLookupService.GetDepotDistributionGroups(appContext.StudySession!.SponsorId!, appContext.StudySession!.StudyId!, appContext.StudySession!.StudyVersion!);

        var groupedShipmentRequestContents = shipmentRequestContents.SelectMany(x => x.Amounts, (sr, amt) => new { sr, amt })
            .GroupBy(grp => new
            {
                grp.sr.ShipmentRequestSettings,
                grp.sr.SourceId,
                grp.sr.KitTypeId,
                grp.amt.LotNumber,
                grp.amt.BatchNumber,
                grp.amt.VendorLotNumber,
                grp.amt.ExpiryDate
            }).Select(x => new FulfillmentStateGroupKey
            {
                ShipmentRequestSettings = x.Key.ShipmentRequestSettings,
                SourceId = x.Key.SourceId,
                KitTypeId = x.Key.KitTypeId,
                LotNumber = x.Key.LotNumber,
                BatchNumber = x.Key.BatchNumber,
                VendorLotNumber = x.Key.VendorLotNumber,
                ExpiryDate = x.Key.ExpiryDate
            }).ToList();

        foreach (var content in groupedShipmentRequestContents)
        {
            var possibleSourceDepotIds = string.IsNullOrWhiteSpace(content.SourceId) ?
                depotDistributionGroups.Where(x => !string.IsNullOrWhiteSpace(x.DepotId)).Select(x => x.DepotId!).Distinct().ToList() :
                new List<string>() { content.SourceId };
            foreach (var sourceDepotId in possibleSourceDepotIds)
            {
                var stateLogsForDepot = await GetStateLogsForDepot(content, sourceDepotId, kitTypes, appContext);
                if (stateLogsForDepot.Any())
                {
                    stateLogs.AddRange(stateLogsForDepot);
                }
            }
        }
        return stateLogs;
    }

    private async Task<List<ShipmentFulfillmentState>> GetStateLogsForDepot(FulfillmentStateGroupKey content, string sourceId, List<KitTypeResponse> kitTypes, FlashApplicationContext appContext)
    {
        var stateLogs = new List<ShipmentFulfillmentState>();

        var inventoryTrackerRequest = GetInventoryTrackerRequestForState(content.KitTypeId, content.LotNumber!, content.BatchNumber!, content.VendorLotNumber!, sourceId, appContext.StudySession!.StudyId!);
        var inventoryResponse = await _inventoryService.GetReleasedInventoryQuantities(inventoryTrackerRequest, appContext);

        var quantityResults = new List<dynamic>();
        if (inventoryResponse?.QuantityResults?.Any() ?? false)
        {
            quantityResults.AddRange(inventoryResponse!.QuantityResults);
        }
        // Convert list of dynamic objects to list of case-insensitive dictionaries
        List<Dictionary<string, object>> quantityResultsDictionaries = quantityResults
            .Select(dyn => new Dictionary<string, object>(JObject.Parse(JsonConvert.SerializeObject(dyn)).ToObject<Dictionary<string, object>>()!, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var currentDepotStateLogs = Enumerable.Repeat(content, 1)
            .Join(kitTypes,
                c => c.KitTypeId,
                kt => kt.Id,
                (c, kt) => new { c, kt })
            .GroupJoin(quantityResultsDictionaries,
                c => new { c.kt.KitType, c.kt.KitTypeCode },
                i => new { KitType = (string?)i[nameof(ComposedAdxKit.KitTypeDescription)], KitTypeCode = (string?)i[nameof(ComposedAdxKit.KitTypeCode)] },
                (c, i) => new { c, i = i.DefaultIfEmpty() })
            .SelectMany(x => x.i, (x, i) => new { x.c, i })
            .Select(x =>
            new ShipmentFulfillmentState()
            {
                KitType = x.i?[nameof(ComposedAdxKit.KitTypeDescription)]?.ToString() ?? x.c.kt.KitType!,
                KitTypeCode = x.i?[nameof(ComposedAdxKit.KitTypeCode)]?.ToString() ?? x.c.kt.KitTypeCode!,
                SequenceRangeStart = string.IsNullOrWhiteSpace(x.i?[nameof(ShipmentFulfillmentState.SequenceRangeStart)]?.ToString()) ? null : long.Parse(x.i?[nameof(ShipmentFulfillmentState.SequenceRangeStart)]?.ToString()!),
                SequenceRangeEnd = string.IsNullOrWhiteSpace(x.i?[nameof(ShipmentFulfillmentState.SequenceRangeEnd)]?.ToString()) ? null : long.Parse(x.i?[nameof(ShipmentFulfillmentState.SequenceRangeEnd)]?.ToString()!),
                SourceId = sourceId,
                LotNumber = x.i?[nameof(ShipmentFulfillmentState.LotNumber)]?.ToString()!,
                BatchNumber = x.i?[nameof(ShipmentFulfillmentState.BatchNumber)]?.ToString()!,
                VendorLotNumber = x.i?[nameof(ShipmentFulfillmentState.VendorLotNumber)]?.ToString()!,
                ExpiryDate = (x.i?[nameof(ShipmentFulfillmentState.ExpiryDate)] != null) ? DateOnly.Parse(x.i?[nameof(ShipmentFulfillmentState.ExpiryDate)]?.ToString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None) : null,
                Study = x.i?[nameof(ShipmentFulfillmentState.Study)] == null ? null! : JsonConvert.DeserializeObject<DrugReleaseStudy>(x.i?[nameof(ShipmentFulfillmentState.Study)]?.ToString()!),
                Quantity = long.TryParse(x.i?[nameof(ShipmentFulfillmentState.Quantity)]?.ToString(), out long quantityVal) ? quantityVal : 0,
                EnvironmentId = appContext.StudySession.Environment!,
                SponsorId = appContext.StudySession.SponsorId!,
                DateTime = DateTimeOffset.UtcNow,
                ShipmentRequestSettings = _mapper.Map<ShipmentFulfillmentStateShipmentRequestSettings>(content.ShipmentRequestSettings)
            }).ToList();
        if (currentDepotStateLogs.Any())
        {
            stateLogs.AddRange(currentDepotStateLogs);
        }
        return stateLogs;
    }

    private static List<(ShipmentRequestContentForFulfillment requestContent, string sourceId, string destinationId, string? bundleGroupId, List<ShipmentRequestModel> shipmentRequests, InventoryTrackerModel inventory)>
    ClassifyShipmentRequestStatuses((bool isFailed, List<(ShipmentRequestContentForFulfillment requestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults) inventory,
            List<(ShipmentRequestContentForFulfillment requestContent, string sourceId, string destinationId, string? bundleGroupId, List<ShipmentRequestModel> shipmentRequests, InventoryTrackerModel inventory)> inventoryForShipments,
            List<(string requestId, string destinationLocationId)> partialShipmentRequestIds, List<(string requestId, string destinationLocationId)> failedShipmentRequestIds, List<(string requestId, string destinationLocationId)> completedShipmentRequestIds, ShipmentRequestContentsForFulfillment requestContent)
    {
        var shipmentRequestIds = requestContent.ShipmentRequests.Select(x => x.Id).ToList();
        if (inventory.invResults.Any() && !inventory.isFailed)
        {
            inventoryForShipments.AddRange(inventory.invResults
                .Select(x => (x.requestContent, x.depotId, requestContent.DestinationId, requestContent.BundleGroupId, requestContent.ShipmentRequests, x.inventory)));
            if (inventory.invResults.Exists(x => x.isPartial))
            {
                partialShipmentRequestIds.AddRange(shipmentRequestIds.Select(id => (id, requestContent.DestinationId)));
            }
            else
            {
                completedShipmentRequestIds.AddRange(shipmentRequestIds.Select(id => (id, requestContent.DestinationId)));
            }
        }
        else
        {
            failedShipmentRequestIds.AddRange(shipmentRequestIds.Select(id => (id, requestContent.DestinationId)));
            // if one part of the request has faile and partial is now allowed, fail the entire shipment request
            if (!(requestContent.ShipmentRequestSettings.AllowPartial ?? false))
            {
                completedShipmentRequestIds.RemoveAll(x => shipmentRequestIds.Contains(x.requestId));
                inventoryForShipments.ForEach(ifs => {
                    ifs.shipmentRequests.RemoveAll(sr => shipmentRequestIds.Contains(sr.Id));
                });
                inventoryForShipments.RemoveAll(ifs => ifs.shipmentRequests.Count == 0);
            }
        }
        return inventoryForShipments;
    }

    private async Task UpdateShipmentRequestStatusesAsync(List<(string requestId, string destinationLocationId)> failedShipmentRequestIds, List<(string requestId, string destinationLocationId)> partialShipmentRequestIds, List<(string requestId, string destinationLocationId)> completedShipmentRequestIds,
        string eventId, FlashApplicationContext appContext, Dictionary<string, string> customDimensions, List<KitTypeResponse> kitTypes)
    {
        var updateTasks = new List<Task>();

        // a shipment request is only failed if it hasn't been split into a partial or processed
        failedShipmentRequestIds.Except(partialShipmentRequestIds).Except(completedShipmentRequestIds).Distinct().ToList().ForEach(srId =>
        {
            updateTasks.Add(PatchShipmentRequestStatus(srId.requestId, ShipmentRequestStatus.Failed, eventId, appContext, customDimensions));
        });

        // include shipment requests that have been split to a failed and completed request when updating partial
        partialShipmentRequestIds = partialShipmentRequestIds.Concat(failedShipmentRequestIds.Intersect(completedShipmentRequestIds)).Distinct().ToList();
        partialShipmentRequestIds.ForEach(srId =>
        {
            updateTasks.Add(PatchShipmentRequestStatus(srId.requestId, ShipmentRequestStatus.Partial, eventId, appContext, customDimensions));
        });

        // a shipment request is only updated to processed status if it hasn't been split to a partial or failed status
        completedShipmentRequestIds.Except(failedShipmentRequestIds).Except(partialShipmentRequestIds).Distinct().ToList().ForEach(srId =>
        {
            updateTasks.Add(PatchShipmentRequestStatus(srId.requestId, ShipmentRequestStatus.Processed, eventId, appContext, customDimensions));
        });

        // mark location as having request fully or partially fulfilled
        var _locationShipmentRequestFulfilledTrackerRepo = await _locationShipmentRequestFulfilledTrackerRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!, eventId);
        var fulfilledTrackerTasks = partialShipmentRequestIds.Concat(completedShipmentRequestIds).GroupBy(x => x.destinationLocationId)
            .Select(async destLocationId =>
            {
                var pk = appContext.StudySession!.GetPartition<LocationShipmentRequestFulfilledTracker>();
                var locationShipmentRequestFulfilledTracker = await _locationShipmentRequestFulfilledTrackerRepo.TryGetAsync(destLocationId.Key, pk);
                if (locationShipmentRequestFulfilledTracker == null)
                {
                    locationShipmentRequestFulfilledTracker = new LocationShipmentRequestFulfilledTracker
                    {
                        Id = destLocationId.Key,
                        StudyId = appContext.StudySession!.StudyId!,
                        SponsorId = appContext.StudySession!.SponsorId!,
                        StudyVersionId = appContext.StudySession!.StudyVersion!,
                        EnvironmentId = appContext.StudySession.Environment!
                    };
                    await _locationShipmentRequestFulfilledTrackerRepo.UpdateAsync(locationShipmentRequestFulfilledTracker, eventId, appContext.CurrentUser, customDimensions);
                }
            });

        updateTasks.AddRange(fulfilledTrackerTasks);

        await Task.WhenAll(updateTasks);
        var distinctShipmentRequestIds = failedShipmentRequestIds.Concat(partialShipmentRequestIds).Concat(completedShipmentRequestIds).GroupBy(x => x.requestId).Select(x => x.Key).ToList();

        // update the pending kit counts for the shipment requests
        await _shipmentRequestService.UpdatePendingKitCountsForShipmentRequestsFulfilled(distinctShipmentRequestIds, eventId, appContext, customDimensions, kitTypes);
    }

    public async Task PatchShipmentRequestStatus(string shipmentRequestId, ShipmentRequestStatus status,
        string eventId, FlashApplicationContext appContext, Dictionary<string, string> customDimensions)
    {
        var shipmentRequestRepo = await _shipmentRequestRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession!.Environment!, eventId);
        var shipmentRequestViewModelRepo =await _shipmentRequestViewRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!, eventId);
        var shipmentRequestSearchClient = await _shipmentRequestSearchManager.GetAzureSearchClient(appContext.StudySession!.SponsorId!, appContext.StudySession!.Environment!, eventId);

        var patchOperations = new List<JsonDiffPatchDotNet.Formatters.JsonPatch.Operation>
        {
            new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation()
            {
                Op = JsonDiffPatchDotNet.Formatters.JsonPatch.OperationTypes.Replace,
                Path = $"/{nameof(ShipmentRequestModel.Status)}",
                Value = status.ToString()
            }
        };
        var patchTask = async () =>
        {
            _ = await shipmentRequestRepo.PatchAsync(shipmentRequestId,
                patchOperations.PatchToCosmosPatchOperation(_appSettings.CosmosInfo!.ComsosSystemKeys),
                appContext.StudySession!.GetPartition<ShipmentRequestModel>(),
                eventId, appContext.CurrentUser, customDimensions);
            var shipmentRequestViewModel = await shipmentRequestViewModelRepo.PatchAsync(shipmentRequestId,
                patchOperations.PatchToCosmosPatchOperation(_appSettings.CosmosInfo!.ComsosSystemKeys),
                appContext.StudySession!.GetPartition<ShipmentRequestViewModel>(),
                eventId, appContext.CurrentUser, customDimensions);

            var searchView = _mapper.Map<ShipmentRequestSearchView>(shipmentRequestViewModel);
            await shipmentRequestSearchClient.MergeOrUploadAsync(new List<ShipmentRequestSearchView> { searchView }, eventId, customDimensions);
        };
        await patchTask();
    }
    private async Task<List<ShipmentModel>> AssignInventoryToShipments(List<(ShipmentRequestContentForFulfillment requestContent, string sourceId, string destinationId, string? bundleGroupId, List<ShipmentRequestModel> shipmentRequests, InventoryTrackerModel inventory)> inventoryForShipments,
        string eventId, FlashApplicationContext appContext, Dictionary<string, string> customDimensions)
    {
        var retryCount = 25;
        var shipmentNumberRetryPolicy = Policy<ShipmentModel>
                .Handle<CosmosException>(ex => ex.StatusCode == HttpStatusCode.Conflict)
                .WaitAndRetryAsync(retryCount, retryAttempt => TimeSpan.FromSeconds(5));
        var kitTypes = await _aggregatorLookupService.GetKitTypes(appContext.StudySession!.SponsorId!, appContext.StudySession!.StudyId!, appContext.StudySession!.StudyVersion!);
        var shipmentsRepo = await _shipmentRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession!.Environment!, eventId);
        var shipmentContentsRepo =await _shipmentContentsRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession!.Environment!, eventId);
        var shipments = new List<ShipmentModel>();
        var shipmentContents = new List<ShipmentContentsModel>();
        var groupedShipmentInventory = inventoryForShipments
            .Join(kitTypes,
                inv => new { inv.inventory.KitType, inv.inventory.KitTypeCode, inv.inventory.BlindingType, inv.inventory.KitManagementType },
                kt => new { kt.KitType, kt.KitTypeCode, BlindingType = (int)EnumExtension.GetEnumFromStringValue<BlindingType>(kt.BlindingType), KitManagementType = (int)EnumExtension.GetEnumFromStringValue<KitManagementType>(kt.KitManagementType) },
                (inv, kt) => new { inv.sourceId, inv.destinationId, inv.bundleGroupId, inv.shipmentRequests, inv.inventory, inv.requestContent, kt })
            .GroupBy(grp => new
            {
                grp.sourceId,
                grp.destinationId,
                grp.bundleGroupId,
                grp.kt.TreatAsAmbient,
                grp.kt.ShippingGroup
            }).ToList();

        foreach (var invForShipment in groupedShipmentInventory)
        {
            var fulfillmentInventoryRecords = new List<InventoryTrackerModel>();
            var (sourceInfo, sourceType) = await _locationLookupService.LookupLocationOrDepotWithLocationTypeByIdAsync(invForShipment.Key.sourceId, appContext);
            var (destinationInfo, destinationType) = await _locationLookupService.LookupLocationOrDepotWithLocationTypeByIdAsync(invForShipment.Key.destinationId, appContext);

            var attemptCount = 0;
            var shipmentModel = await shipmentNumberRetryPolicy.ExecuteAsync(async () =>
            {
                attemptCount++;
                var sequentialNumber = GetNextShipmentSequentialNumber(appContext);
                if (attemptCount >= retryCount - 3)
                {
                    sequentialNumber += attemptCount - (retryCount - 3) + 1;
                }
                var shipment = new ShipmentModel()
                {
                    Status = ShipmentStatus.Created,
                    StatusName = ShipmentStatus.Created.ToString(), // This is used for history and FE expects the enum name and not value, which they convert to the display value
                    ShipmentNumber = sequentialNumber.ToString().PadLeft(11, '0'),
                    SequentialNumber = sequentialNumber,
                    EnvironmentId = appContext.StudySession!.Environment!,
                    SponsorId = appContext.StudySession.SponsorId!,
                    StudyId = appContext.StudySession.StudyId!,
                    StudyVersionId = appContext.StudySession.StudyVersion!,
                    SourceId = invForShipment.Key.sourceId,
                    SourceType = sourceType,
                    SourceInfo = sourceInfo,
                    DestinationId = invForShipment.Key.destinationId,
                    DestinationType = destinationType,
                    DestinationInfo = destinationInfo,
                    RequestDates = invForShipment.SelectMany(x => x.shipmentRequests).GroupBy(grp => grp.StartDate).Select(x => x.Key).ToList(),

                    ApprovedDates = invForShipment.SelectMany(x => x.shipmentRequests).Where(r => r.DateApproved.HasValue).Select(r => (DateTimeOffset)r.DateApproved!).Distinct().ToList(),

                    ShipmentType = string.Join(", ", invForShipment.SelectMany(x => x.shipmentRequests).GroupBy(grp => grp.RequestType).Select(x => x.Key).ToList()),
                    ShipmentRequestIds = invForShipment.SelectMany(x => x.shipmentRequests).GroupBy(grp => grp.Id).Select(x => x.Key).ToList(), // flatten out grouped shipment requestIds.
                    IsTemperatureMonitored = !invForShipment.Key.TreatAsAmbient,
                };
                // need to commit shipment immediately so that audit record updates autonumber
                return await shipmentsRepo.CreateAsync(shipment, eventId, appContext.CurrentUser, customDimensions);
            });

            // process each kit type and source location separately
            foreach (var inventoryByManagementType in invForShipment.GroupBy(grp => new { grp.kt.KitManagementType, KitTypeId = grp.kt.Id, grp.sourceId })
                .Select(x => new FulfillmentInventoryByTypeAndSource()
                {
                    SourceId = x.Key.sourceId,
                    KitManagementType = x.Key.KitManagementType,
                    KitTypeId = x.Key.KitTypeId,
                    Inventory = x.Select(z => (z.inventory, z.requestContent)).ToList()
                }).ToList())
            {
                if (inventoryByManagementType.KitManagementType == KitManagementType.Numbered.ToString().ToLower())
                {
                    var serializedFulfilledInventory = await UpdateSerializedInventoryForShipment(inventoryByManagementType, shipmentModel.Id, shipmentModel.DestinationId, eventId, appContext, customDimensions);
                    if (serializedFulfilledInventory.Select(x => x.Id).Except(inventoryByManagementType.Inventory.Select(x => x.Inventory.Id)).Any())
                    {
                        throw new FlashException($"Unable to update serialized inventory. Expected: {JsonConvert.SerializeObject(inventoryByManagementType)} Actual: {JsonConvert.SerializeObject(serializedFulfilledInventory)}");
                    }
                    fulfillmentInventoryRecords.AddRange(serializedFulfilledInventory);
                }
                else
                {
                    var bulkFulfilledInventory = await UpdateBulkInventoryForShipment(inventoryByManagementType, shipmentModel.Id, shipmentModel.DestinationId, eventId, appContext, customDimensions);
                    fulfillmentInventoryRecords.AddRange(bulkFulfilledInventory);
                }
            }
            shipmentContents.AddRange(fulfillmentInventoryRecords
                .Join(kitTypes,
                inv => new { inv.KitType, inv.KitTypeCode },
                kt => new { kt.KitType, kt.KitTypeCode },
                (inv, kt) => new { inventory = inv, kt })
                .Where(x => x.inventory.ShipmentId == shipmentModel.Id)
                .Select(x =>
                    new ShipmentContentsModel()
                    {
                        InventoryTrackerModelId = x.inventory.Id,
                        Quantity = x.inventory.Quantity,
                        ShipmentId = shipmentModel.Id,
                        LotNumber = x.inventory.LotNumber,
                        ExpiryDate = DateOnly.TryParse(x.inventory.ExpiryDate?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expiryDateValue) ? expiryDateValue : DateOnly.MinValue,
                        KitNumber = x.inventory.KitNumber,
                        KitTypeId = x.kt.Id,
                        BatchNumber = x.inventory.BatchNumber,
                        VendorLotNumber = x.inventory.VendorLotNumber,
                        SequenceNumber = x.inventory.SequenceNumber,
                        EnvironmentId = appContext.StudySession!.Environment!,
                        SponsorId = appContext.StudySession.SponsorId!,
                        StudyId = appContext.StudySession.StudyId!,
                        StudyVersionId = appContext.StudySession.StudyVersion!
                    }
                ).ToList());

            shipmentModel.Quantity = shipmentContents
                .Where(x => x.ShipmentId == shipmentModel.Id)
                .Sum(x => x.Quantity);

            await PatchShipmentQuantity(shipmentModel.Id, shipmentModel.Quantity, shipmentModel.PartitionKey, eventId, shipmentsRepo, appContext.CurrentUser, customDimensions);
            shipments.Add(shipmentModel);
        }
        await shipmentContentsRepo.CreateAsync(shipmentContents, eventId, appContext.CurrentUser, customDimensions);
        return shipments;
    }

    private async Task PatchShipmentQuantity(string shipmentId, int quantity, string partitionKey, string eventId, IDataRepository<ShipmentModel> shipmentsRepo, UserSession userSession, Dictionary<string, string> customDimensions)
    {
        var patchOperations = new List<JsonDiffPatchDotNet.Formatters.JsonPatch.Operation>
        {
            new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation()
            {
                Op = JsonDiffPatchDotNet.Formatters.JsonPatch.OperationTypes.Replace,
                Path = $"/{nameof(ShipmentModel.Quantity)}",
                Value = quantity
            }
        };
        _ = await shipmentsRepo.PatchAsync(shipmentId,
            patchOperations.PatchToCosmosPatchOperation(_appSettings.CosmosInfo!.ComsosSystemKeys),
            partitionKey,
            eventId, userSession, customDimensions);
    }
    private long GetNextShipmentSequentialNumber(FlashApplicationContext appContext)
    {
        return _identityGenerator.GetNextId($"{appContext.StudySession!.SponsorId}/{appContext.StudySession.Environment}:{nameof(ShipmentModel)}");
    }
    private async Task<List<InventoryTrackerModel>> UpdateSerializedInventoryForShipment(FulfillmentInventoryByTypeAndSource inventoryByManagementType, string shipmentId, string destinationLocationId, string eventId, FlashApplicationContext appContext, Dictionary<string, string> customDimensions)
    {
        var inventoryTrackerRequest = new InventoryTrackerRequest()
        {
            InventoryTrackerIds = inventoryByManagementType.Inventory.Select(x => x.Inventory.Id).ToList(),
            Status = InventoryStatus.Available,
            LocationId = inventoryByManagementType.SourceId, // is still at the source location
            ShipmentId = string.Empty // is not in a shipment
        };
        var inventoryTrackerUpdateRequest = new InventoryTrackerUpdateRequest()
        {
            ShipmentId = shipmentId,
            Status = InventoryStatus.Pending,
            DestinationLocationId = destinationLocationId
        };

        var serializedUpdateResponse = await _inventoryService.UpdateInventory(inventoryTrackerRequest, inventoryTrackerUpdateRequest, appContext, customDimensions, eventId);
        if (serializedUpdateResponse?.Status != InventoryTrackerRequestStatus.Success) throw new FlashException("Unable to update inventory");

        return serializedUpdateResponse.Results;
    }

    private async Task<List<InventoryTrackerModel>> UpdateBulkInventoryForShipment(FulfillmentInventoryByTypeAndSource inventoryByManagementType, string shipmentId, string destinationLocationId, string eventId, FlashApplicationContext appContext, Dictionary<string, string> customDimensions)
    {
        var requestContents = inventoryByManagementType.Inventory.GroupBy(grp => grp.RequestContent).ToList();
        var fulfillmentInventoryRecords = new Dictionary<string, InventoryTrackerModel>();
        var consumedInventoryRemaining = new Dictionary<string, int>();
        foreach (var requestContent in requestContents)
        {
            var totalQuantityToFulfill = requestContent.Key.FulfilledQuantity;
            var remainingQuantityToFulfill = totalQuantityToFulfill;
            foreach (var bulkInv in requestContent)
            {
                var quantityRemainingInInventory = consumedInventoryRemaining.TryGetValue(bulkInv.Inventory.Id, out var consumedQuantity) ? consumedQuantity : bulkInv.Inventory.Quantity;
                var quantityToFulfill = quantityRemainingInInventory < remainingQuantityToFulfill ? quantityRemainingInInventory : remainingQuantityToFulfill;
                if (quantityToFulfill == 0 || quantityRemainingInInventory == 0) continue; // if there is nothing left to fulfill from this inventory record, move to next
                remainingQuantityToFulfill -= quantityToFulfill;
                var inventoryTrackerRequest = new InventoryTrackerRequest()
                {
                    InventoryTrackerIds = new List<string> { bulkInv.Inventory.Id },
                    Status = InventoryStatus.Available,
                    LocationId = inventoryByManagementType.SourceId, // is still at the source location
                    ShipmentId = string.Empty // is not in a shipment
                };
                var inventoryTrackerUpdateRequest = new InventoryTrackerUpdateRequest()
                {
                    ShipmentId = shipmentId,
                    Status = InventoryStatus.Pending,
                    Quantity = int.Parse(Math.Min(int.MaxValue, quantityToFulfill).ToString()),
                    DestinationLocationId = destinationLocationId
                };

                var bulkUpdateResponse = await _inventoryService.UpdateInventory(inventoryTrackerRequest, inventoryTrackerUpdateRequest, appContext, customDimensions, eventId);
                if (bulkUpdateResponse?.Status != InventoryTrackerRequestStatus.Success) throw new FlashException("Unable to update inventory");
                // this will include bulk inventory records updated, including the new ones not assigned to the shipment
                // the ones that do not have the shipmentId are filterd out here
                fulfillmentInventoryRecords.AddOrSetRange(bulkUpdateResponse.Results
                    .Where(x => x.ShipmentId == shipmentId)
                    .Select(x => new KeyValuePair<string, InventoryTrackerModel>(x.Id, x)));
                consumedInventoryRemaining.AddOrSetRange(bulkUpdateResponse.Results
                    .Where(x => x.ShipmentId != shipmentId)
                    .Select(x => new KeyValuePair<string, int>(x.Id, x.Quantity)));
                if (fulfillmentInventoryRecords.Sum(x => x.Value.Quantity) == totalQuantityToFulfill)
                {
                    // already fulfilled the requested quantity
                    break;
                }
            }
        }
        return fulfillmentInventoryRecords.Select(x => x.Value).ToList();
    }
#pragma warning disable S107
    private async Task<List<string>> GetSourceDepotIds(
        List<DistributionGroupResponse> distributionGroups,
        string? siteCountryCode,
        string distributionDestinationType,
        string distributionDestination,
        string kitTypeId,
        string sponsorId,
        string studyId,
        string studyVersionId)
    {
        var response = new List<string>();
        if (distributionDestinationType == DistributionDestinationType.Depot)
        { // if site country code is null, the recipient is a depot
            response = distributionGroups!.Where(x => x.Destinations.Contains(distributionDestination) &&
                x.DestinationType == DistributionDestinationType.Depot)
                .Select(x => x.DepotId!.ToString()).ToList();
        }
        else if (distributionDestinationType == DistributionDestinationType.Country && !string.IsNullOrWhiteSpace(siteCountryCode))
        {
            var countryDepotPriorities = await _aggregatorLookupService.GetCountryDepotPriorities(sponsorId, studyId, studyVersionId);
            var countryDepotOptions = countryDepotPriorities?.CountryDepots?.Find(x => x.CountryCode == siteCountryCode)?.DepotOptions ?? new List<DepotOption>();
            var countryLevelKitTypeDistribution = distributionGroups?.Where(x => x.DestinationType == Constants.DistributionDestinationType.Country && x.Level == Constants.KitTypeDistributionLevel.Kit)
                .Where(x => x.KitTypeConfigs?.Exists(y => y.KitTypeIds.Contains(kitTypeId)) ?? false).ToList() ?? new List<DistributionGroupResponse>();
            // return all countries that are set up for study level or kit type level distribution
            var validDepotIdsForKitTypes = countryLevelKitTypeDistribution
                .SelectMany(x => x.KitTypeConfigs?.Where(y => y.KitTypeIds.Contains(kitTypeId)) ?? new List<DistributionGroupKitTypeConfig>(), (distributionGroup, kitTypeConfig) => new { distributionGroup, kitTypeConfig })
                .Where(x => x.distributionGroup.Destinations.Contains(siteCountryCode))
                .Select(x => x.distributionGroup.DepotId).ToList();
            var validDepotIdsForStudyLevel = distributionGroups?.Where(x => x.DestinationType == Constants.DistributionDestinationType.Country && x.Level == Constants.KitTypeDistributionLevel.Study)
                    .Where(x => x.Destinations.Contains(siteCountryCode))
                    .Select(x => x.DepotId).ToList() ?? new List<string?>();
            response = validDepotIdsForKitTypes.Concat(validDepotIdsForStudyLevel)
                .GroupBy(x => x).Select(x => x.Key)
                .Join(countryDepotOptions,
                    dx => dx,
                    cdo => cdo.DepotId,
                    (dx, cdo) => new { dx, cdo })
                .OrderBy(x => x.cdo.Priority)
                .Select(x => x.dx!).ToList();
        }
        return response;
    }

    private async Task<(bool isFailed, List<(ShipmentRequestContentForFulfillment shipmentRequestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults)> GetInventoryForShipmentRequestContents(
        List<ShipmentRequestContentsForFulfillment> shipmentRequestContents,
        string destinationId,
        string destinationType,
        CountryDepotPriorityView countryDepotPriorities,
        List<DistributionGroupResponse> depotDistributionGroups, string distributionDestinationType, string depotDistributionDestination,
        List<KitTypeResponse> kitTypes,
        ShipmentRequestContentsForFulfillment currentShipmentRequestContents, string? siteCountryCode,
        ShipmentRequestSettings shipmentRequestSettings, List<(int quantityRemaining, InventoryTrackerModel inv)>? usedInventory,
        FlashApplicationContext appContext,
        string? sourceId)
    {
        var response = (isFailed: false, invResults: new List<(ShipmentRequestContentForFulfillment shipmentRequestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)>());
        var isDepotFound = false;
        var sourceDepotIds = string.IsNullOrWhiteSpace(sourceId) ?
            await GetSourceDepotIds(depotDistributionGroups, siteCountryCode, distributionDestinationType, depotDistributionDestination, currentShipmentRequestContents.KitTypeId, appContext.StudySession!.SponsorId!, appContext.StudySession.StudyId!, appContext.StudySession.StudyVersion!) :
            new List<string>() { sourceId };
        var expiryLimits = await GetExpiryLimits(appContext.StudySession!.SponsorId!, appContext.StudySession.StudyId!, appContext.StudySession.StudyVersion!);

        var checkedDepotIds = new List<string>();
        while (!isDepotFound && !response.isFailed)
        {
            var sourceDepotId = ShipmentFulfillmentHelper.GetNextDepotPriority(sourceDepotIds, checkedDepotIds);
            if (sourceDepotId == null)
            {
                response.isFailed = true;
                continue;
            }
            else
            {
                checkedDepotIds.Add(sourceDepotId);
                isDepotFound = true;
            }

            var kitType = kitTypes.First(x => x.Id == currentShipmentRequestContents.KitTypeId);
            var leadTime = await _shipmentService.GetDepotLeadTimeByStudyLocation(destinationId, kitType, countryDepotPriorities,
                depotDistributionGroups, appContext, sourceDepotId);
            foreach (var amount in currentShipmentRequestContents.Amounts)
            {
                var inventoryTrackerRequest = GetInventoryTrackerRequestForFulfillment(destinationType, amount, kitType, sourceDepotId, leadTime, shipmentRequestSettings, expiryLimits,
                    usedInventory, appContext.StudySession!.StudyId!, (shipmentRequestSettings.RequireCountryApproval ?? false) ? siteCountryCode : null!);
                var inventoryResponse = await _inventoryService.GetReleasedInventory(inventoryTrackerRequest, appContext);
                if (inventoryResponse?.Results.Any(x => x.KitManagementType == (int)KitManagementType.Numbered) ?? false)
                {
                    usedInventory?.AddRange(inventoryResponse.Results.Where(x => x.KitManagementType == (int)KitManagementType.Numbered).Select(x => (0, x)));
                }

                (isDepotFound, response.isFailed, response.invResults) = await UpdateRequestAmounts(
                    shipmentRequestContents,
                    response.isFailed, response.invResults, inventoryResponse, amount, kitType, kitTypes,
                    sourceDepotId, shipmentRequestSettings, expiryLimits, appContext, siteCountryCode, destinationType, destinationId, countryDepotPriorities, depotDistributionGroups);
            }
            if (!(shipmentRequestSettings.UseBackupDepots ?? false) && !isDepotFound)
            {
                response.isFailed = true; // only check for next depot if UseBackupDepots is enabled for shipment request type
            }
        }
        return response;
    }

    private async Task<(bool isDepotFound, bool responseIsFailed, List<(ShipmentRequestContentForFulfillment shipmentRequestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults)> UpdateRequestAmounts(
        List<ShipmentRequestContentsForFulfillment> shipmentRequestContents,
        bool isFailed,
        List<(ShipmentRequestContentForFulfillment shipmentRequestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults,
        InventoryTrackerRequestReply? inventoryResponse,
        ShipmentRequestContentForFulfillment requestContent,
        KitTypeResponse kitType, List<KitTypeResponse> kitTypes,
        string sourceDepotId,
        ShipmentRequestSettings shipmentRequestSettings,
        ExpiryLimitView expiryLimits,
        FlashApplicationContext appContext,
        string? siteCountryCode,
        string destinationType,
        string destinationId,
        CountryDepotPriorityView countryDepotPriorities,
        List<DistributionGroupResponse>? depotDistributionGroups)
    {
        var isDepotFound = true;
        if (inventoryResponse?.Status == InventoryTrackerRequestStatus.Success && (inventoryResponse.Results?.Exists(x => x.Quantity > 0) ?? false) && inventoryResponse.Results.Sum(x => x.Quantity) >= requestContent.RequestedQuantity)
        {
            requestContent.FulfilledQuantity = requestContent.RequestedQuantity;
            requestContent.IsProcessed = true;
            invResults.AddRange(inventoryResponse.Results.Select(inv => (requestContent, sourceDepotId, inv, isPartial: false)));
            (isFailed, invResults) = await GetExtraInventoryForShipmentBlinding(
                shipmentRequestContents,
                destinationId, destinationType,
                isFailed, invResults, appContext, requestContent, kitType, kitTypes,
                sourceDepotId, shipmentRequestSettings, expiryLimits, inventoryResponse.Results.Select(invR => (0, invR)).ToList(), siteCountryCode,
                countryDepotPriorities, depotDistributionGroups);
        }
        else if ((shipmentRequestSettings.AllowPartial ?? false) && (inventoryResponse?.Results?.Exists(x => x.Quantity > 0) ?? false))
        {
            requestContent.FulfilledQuantity = inventoryResponse!.Results?.Sum(x => x.Quantity) ?? 0;
            requestContent.IsProcessed = true;
            invResults.AddRange(inventoryResponse.Results!.Select(inv => (requestContent, sourceDepotId, inv, isPartial: true)));
            (isFailed, invResults) = await GetExtraInventoryForShipmentBlinding(
                shipmentRequestContents,
                destinationId, destinationType,
                isFailed, invResults, appContext, requestContent, kitType, kitTypes,
                sourceDepotId, shipmentRequestSettings, expiryLimits, inventoryResponse.Results!.Select(invR => (0, invR)).ToList(), siteCountryCode,
                countryDepotPriorities, depotDistributionGroups);
        }
        else
        {
            isDepotFound = false; // if partial wasn't allowed and there wasn't enough inventory, check next depot for inventory
        }

        if (isFailed || !isDepotFound)
        {
            requestContent.IsProcessed = false;
            requestContent.FulfilledQuantity = 0;
            invResults.Clear();
        }

        return (isDepotFound, isFailed, invResults);
    }

    private async Task<(bool isFailed, List<(ShipmentRequestContentForFulfillment requestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults)> GetExtraInventoryForShipmentBlinding(
        List<ShipmentRequestContentsForFulfillment> shipmentRequestContents, string destinationId,
        string destinationType, bool isFailed,
        List<(ShipmentRequestContentForFulfillment shipmentRequestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults,
        FlashApplicationContext appContext, ShipmentRequestContentForFulfillment requestContent,
        KitTypeResponse kitType, List<KitTypeResponse> kitTypes,
        string sourceDepotId,
        ShipmentRequestSettings shipmentRequestSettings,
        ExpiryLimitView expiryLimits, List<(int quantityRemaining, InventoryTrackerModel inv)>? usedInventory, string? siteCountryCode,
        CountryDepotPriorityView countryDepotPriorities, List<DistributionGroupResponse>? depotDistributionGroups)
    {
        var requestContentsToCheck = shipmentRequestContents
            .Join(kitTypes,
                src => src.KitTypeId,
                kt => kt.Id,
            (src, kt) => new { kt, src })
            .Where(x => x.kt.KitBlindingGroup == kitType.KitBlindingGroup)
            .Select(x => new {
                FulfilledQuantity = x.src.Amounts.Sum(z => z.FulfilledQuantity),
                IsProcessed = x.src.Amounts.TrueForAll(z => z.IsProcessed)
            }).ToList();
        if (kitType.BlindingType?.ToLower() == BlindingType.OpenLabel.ToString().ToLower() ||
            !(shipmentRequestSettings.SingleKitBlinding ?? false) ||
            !requestContentsToCheck.Any() ||
            requestContentsToCheck.Sum(x => x.FulfilledQuantity) > 1 || // if we already fulfilled more than 1
            !requestContentsToCheck.TrueForAll(x => x.IsProcessed) // or if we haven't yet processed all request amounts for the blinding group then can skip adding an extra kit
            )
        {
            // don't need to get extra inventory if not enabled or if we already have enough inventory
            return (isFailed, invResults);
        }
        return await SetExtraInventoryForBlinding(destinationType, destinationId, isFailed, invResults, appContext, requestContent, kitTypes, kitType, sourceDepotId, shipmentRequestSettings, expiryLimits, usedInventory, siteCountryCode, countryDepotPriorities, depotDistributionGroups);
    }
    private async Task<(bool isFailed, List<(ShipmentRequestContentForFulfillment requestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults)>  SetExtraInventoryForBlinding(string destinationType, string destinationId, bool isFailed,
        List<(ShipmentRequestContentForFulfillment requestContent, string depotId, InventoryTrackerModel inventory, bool isPartial)> invResults,
        FlashApplicationContext appContext, ShipmentRequestContentForFulfillment requestContent, List<KitTypeResponse> kitTypes, KitTypeResponse kitType, string sourceDepotId, ShipmentRequestSettings shipmentRequestSettings,
        ExpiryLimitView expiryLimits, List<(int quantityRemaining, InventoryTrackerModel inv)>? usedInventory, string? siteCountryCode, CountryDepotPriorityView countryDepotPriorities, List<DistributionGroupResponse>? depotDistributionGroups)
    {
        InventoryTrackerModel extraInventoryRecord = null!;
        var blindingGroupKitTypes = new List<KitTypeResponse>();
        if (kitType.SingleKitShipmentBlinding?.ToLower() == SingleKitBlindingType.GroupBased.ToString().ToLower())
        {
            blindingGroupKitTypes = kitTypes.Where(x => x.ShippingGroup == kitType.ShippingGroup && // when picking an extra kit for blinding must be in the same shipping group
                                                        x.KitBlindingGroup == kitType.KitBlindingGroup && x.Id != kitType.Id).ToList(); // and blinding group, but not same kit type
        }
        else if (kitType.SingleKitShipmentBlinding?.ToLower() == SingleKitBlindingType.KitBased.ToString().ToLower())
        {
            blindingGroupKitTypes = kitTypes.Where(x => x.ShippingGroup == kitType.ShippingGroup && // when picking an extra kit for blinding must be in the same shipping group
                                                        x.Id == kitType.Id).ToList(); // and kit type
        }

        var addedKit = false;
        foreach (var blindingGroupKitType in blindingGroupKitTypes)
        {
            var kitTypeForKitBlindingGroup = kitTypes.Single(x => x.Id == blindingGroupKitType.Id);
            // if there is enough inventory for the extra kit already in results, increase quantity fulfilled
            var possibleKitsToIncrease = invResults.Where(x => x.inventory.KitType == kitTypeForKitBlindingGroup.KitType &&
                x.inventory.KitTypeCode == kitTypeForKitBlindingGroup.KitTypeCode).ToList();
            if (possibleKitsToIncrease.Any() && possibleKitsToIncrease.Sum(x => x.inventory.Quantity) > possibleKitsToIncrease[0].requestContent.FulfilledQuantity)
            {
                var invResultsToReplace = invResults.Intersect(possibleKitsToIncrease);
                invResults = invResults.Except(invResultsToReplace).ToList();
                requestContent.FulfilledQuantity++;
                invResults.AddRange(invResultsToReplace!.Select(x => {
                    x.requestContent.FulfilledQuantity = requestContent.FulfilledQuantity;
                    return x;
                }));
                addedKit = true;
                break; // once we find a record to increase we can stop
            }
            else // see if there is another inventory record to fulfill from
            {
                var newBlindingRequestAmount = JsonConvert.DeserializeObject<ShipmentRequestContentForFulfillment>(JsonConvert.SerializeObject(requestContent))!;
                newBlindingRequestAmount.RequestedQuantity = 1;
                var leadTime = await _shipmentService.GetDepotLeadTimeByStudyLocation(destinationId, kitType, countryDepotPriorities,
                    depotDistributionGroups!, appContext, sourceDepotId);
                var inventoryTrackerRequest = GetInventoryTrackerRequestForFulfillment(destinationType, newBlindingRequestAmount, kitTypeForKitBlindingGroup, sourceDepotId, leadTime, shipmentRequestSettings, expiryLimits,
                    usedInventory, appContext.StudySession!.StudyId!, (shipmentRequestSettings.RequireCountryApproval ?? false) ? siteCountryCode : null!);
                var inventoryResponse = await _inventoryService.GetReleasedInventory(inventoryTrackerRequest, appContext);
                extraInventoryRecord = inventoryResponse?.Results?.FirstOrDefault()!;
                if (extraInventoryRecord != null)
                {
                    invResults.Add((newBlindingRequestAmount, sourceDepotId, extraInventoryRecord, isPartial: false));
                    addedKit = true;
                    break; // once we find a new record we can stop
                }
            }
        }
        return ((!addedKit) || isFailed, invResults);
    }

    private static InventoryTrackerRequest GetInventoryTrackerRequestForFulfillment(string destinationType, ShipmentRequestContentForFulfillment requestContent, KitTypeResponse kitType, string sourceDepotId, int leadTime, ShipmentRequestSettings shipmentRequestSettings,
        ExpiryLimitView expiryLimits, List<(int quantityRemaining, InventoryTrackerModel inv)>? usedInventory, string? studyId = null, string? countryCode = null)
    {
#pragma warning restore S107
        var doNotShipDays = destinationType == LocationType.Site ? (expiryLimits?.KitGroups?.Find(x => x.KitTypeIds.Contains(kitType.Id))?.DoNotShipDays ?? 0) : 0;
        var doNotShipExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays((shipmentRequestSettings.EnforcesDns ?? false) ? (doNotShipDays + leadTime) : 1));
        var minExpiryDate = requestContent.ExpiryDate.HasValue ? requestContent.ExpiryDate.Value : doNotShipExpiryDate;

        return new InventoryTrackerRequest()
        {
            KitTypeId = kitType.Id,
            BlindingType = EnumExtension.GetEnumFromStringValue<BlindingType>(kitType.BlindingType),
            Status = InventoryStatus.Available,
            LocationId = sourceDepotId,
            LotNumber = string.IsNullOrWhiteSpace(requestContent.LotNumber) ? null :requestContent.LotNumber,
            BatchNumber = string.IsNullOrWhiteSpace(requestContent.BatchNumber) ? null : requestContent.BatchNumber,
            VendorLotNumber = string.IsNullOrWhiteSpace(requestContent.VendorLotNumber) ? null : requestContent.VendorLotNumber,
            ManufacturingLotNumbers = (requestContent.MfgLotNumber?.Exists(mfgLot => !string.IsNullOrWhiteSpace(mfgLot)) ?? false) ? null : requestContent.MfgLotNumber,
            SequenceNumberStart = requestContent.SeqStartRange,
            SequenceNumberEnd = requestContent.SeqEndRange,
            MaxSerializedQuantity = int.Parse(Math.Min(int.MaxValue, requestContent.RequestedQuantity).ToString()),
            MinExpiryDate = minExpiryDate,
            OrderByValue = $"todatetime({nameof(InventoryTrackerParquet.ExpiryDate)}) asc, tolong({nameof(InventoryTrackerParquet.SequenceNumber)}) asc",
            StudyId = studyId,
            CountryCode = countryCode,
            ShipmentId = string.Empty, // is not in a shipment
            ExcludeInventoryTrackerIds = usedInventory?.Where(x => x.quantityRemaining <= 0).Select(x => x.inv.Id).ToList(),
            MinQuantity = 1 // don't pull bulk records that have zero quantity left
        };
    }

    private static InventoryTrackerRequest GetInventoryTrackerRequestForState(string kitTypeId, string lotNumber, string batchNumber, string vendorLotNumber, string sourceDepotId, string? studyId = null)
    {
        return new InventoryTrackerRequest() {
                    KitTypeId = kitTypeId,
                    Status = InventoryStatus.Available,
                    LocationId = sourceDepotId,
                    LotNumber = string.IsNullOrWhiteSpace(lotNumber) ? null : lotNumber,
                    BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber,
                    VendorLotNumber = string.IsNullOrWhiteSpace(vendorLotNumber) ? null : vendorLotNumber,
                    OrderByValue = $"todatetime({nameof(InventoryTrackerParquet.ExpiryDate)}) asc, tolong({nameof(InventoryTrackerParquet.SequenceNumber)}) asc",
                    StudyId = studyId,
                    ShipmentId = string.Empty, // is not in a shipment
                    MinQuantity = 1, // don't pull bulk records that have zero quantity left,
                    QuantityRequest = new QuantityRequest() {
                        GroupByProperties = new List<string>()
                        {
                            nameof(ComposedAdxKit.KitTypeDescription),
                            nameof(ComposedAdxKit.KitTypeCode),
                            nameof(ComposedAdxKit.LotNumber),
                            nameof(ComposedAdxKit.BatchNumber),
                            nameof(ComposedAdxKit.VendorLotNumber),
                            nameof(ComposedAdxKit.ExpiryDate)
                        },
                IncludeSequenceRangeInResponse = true,
                IncludeDrugReleaseStudyInResponse = true
            }
        };
    }

    #endregion

    private class FulfillmentInventoryByTypeAndSource
    {
        public string KitTypeId { get; set; } = null!;
        public string KitManagementType { get; set; } = null!;
        public string SourceId { get; set; } = null!;
        public List<(InventoryTrackerModel Inventory, ShipmentRequestContentForFulfillment RequestContent)> Inventory { get; set; } = new List<(InventoryTrackerModel Inventory, ShipmentRequestContentForFulfillment requestContent)>();
    }

    private class FulfillmentStateGroupKey
    {
        public ShipmentRequestSettings ShipmentRequestSettings { get; set; } = null!;
        public string? SourceId { get; set; }
        public string KitTypeId { get; set; } = null!;
        public string? LotNumber { get; set; }
        public string? BatchNumber { get; set; }
        public string? VendorLotNumber { get; set; }
        public DateOnly? ExpiryDate { get; set; }
    }

}
