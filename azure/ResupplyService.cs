using Azure.Storage.Blobs.Specialized;
using Endpoint.Flash.Aggregator.Domain;
using Endpoint.Flash.Aggregator.Domain.Design;
using Endpoint.Flash.Aggregator.Service.Helper;
using Endpoint.Flash.GlobalData.Domain.Location.StudyDepot;
using Endpoint.Flash.Inventory.Domain.ExpiryLimit;
using Endpoint.Flash.Inventory.Domain.SupplySetting;
using Endpoint.Flash.Shipment.Domain.Resupply;
using Endpoint.Flash.Shipment.Functions.Services;
using Endpoint.Flash.StudySettings.Domain.ShipmentSetting;
using System.Data;
using System.Text;
using Endpoint.Flash.PatientsDomain.VisitSchedule;
using Endpoint.Flash.Shipment.Functions.ShipmentFulfillment;
using Endpoint.Flash.Patients.Runtime.Domain.Visit.SearchIndex;
using static Endpoint.Flash.Patients.Domain.AppConstant;
using Endpoint.Flash.PatientsDomain.VisitSchedule.Model;
using Endpoint.Flash.Core.Extensions.AzureSearch.Interface;
using Endpoint.Flash.Core.Extensions.AzureSearch;
using Endpoint.Flash.GlobalData.Domain.Location;
using Endpoint.Flash.GlobalData.Domain.Location.StudyLocation;

namespace Endpoint.Flash.Shipment.Functions.Resupply.Services
{
    public class ResupplyService : IResupplyService
    {
        private readonly IMediator _mediator;
        private readonly ApplicationSettings _settings;
        private readonly IAzureSearchWithHttpApi _azureSearchWithHttpApi;
        private readonly IShipmentService _shipmentService;
        private readonly IStateBlobServiceClient _resupplyStateBlobServiceClient;
        private readonly IDataRepositoryManager<ResupplyStatusViewModel> _resupplyStatusDataRepositoryManager;

#pragma warning disable S107 // Methods should not have too many parameters
        public ResupplyService(IMediator mediator,
            ApplicationSettings settings,
            IAzureSearchWithHttpApi azureSearchWithHttpApi,
            IShipmentService shipmentService,
            IStateBlobServiceClient resupplyStateBlobServiceClient,
            IDataRepositoryManager<ResupplyStatusViewModel> resupplyStatusDataRepositoryManager)
        {
#pragma warning restore S107 // Methods should not have too many parameters
            _mediator = mediator;
            _settings = settings;
            _azureSearchWithHttpApi = azureSearchWithHttpApi;
            _shipmentService = shipmentService;
            _resupplyStateBlobServiceClient = resupplyStateBlobServiceClient;
            _resupplyStatusDataRepositoryManager = resupplyStatusDataRepositoryManager;
        }

        public async Task<List<SupplySettingView>> GetSupplySettings(FlashApplicationContext applicationContext, string supplySettingId = null)
        {
            var config = new StudyDesignDomain();

            await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, applicationContext, _settings,
                new List<string> { AppConstants.Entity.Design.SupplySettings },
                config, supplySettingId!, skipBehavior: true, bypassCache: true);

            return config.SupplySettings;
        }

        public async Task<List<VisitDispensationComboGroupResponse>> GetVisitDispensationComboGroups(FlashApplicationContext applicationContext)
        {
            var config = new StudyDesignDomain();

            await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, applicationContext, _settings,
                new List<string> { AppConstants.Entity.Design.DispensationComboGroups },
                config, skipBehavior: true);

            return config.DispensationComboGroups;
        }

        public async Task<List<VisitDispensationComboResponse>> GetVisitDispensationCombos(FlashApplicationContext applicationContext)
        {
            var config = new StudyDesignDomain();

            await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, applicationContext, _settings,
                new List<string> { AppConstants.Entity.Design.DispensationCombos },
                config, skipBehavior: true);

            return config.DispensationCombos;
        }

        public async Task<IEnumerable<StudyLocationSearchView>> GetStudyLocations(FlashApplicationContext applicationContext)
        {
            // has index been created yet?
            var indexExists = await _azureSearchWithHttpApi.IndexTrackerExists(nameof(StudyLocationSearchView));

            //get the aggregated counts based on calculated expiry
            Expression<Func<StudyLocationSearchView, bool>> filterExpression =
                d => d.SponsorId == applicationContext.StudySession!.SponsorId &&
                     d.EnvironmentId == applicationContext.StudySession.Environment &&
                     d.StudyId == applicationContext.StudySession.StudyId;

            var filterQuery = QueryExtension.CreateQuery(filterExpression).First();
            var queryParameters = new Dictionary<string, string>();
            queryParameters.AddQueryParametersForAzureSearch(filterQuery);
            var data = !indexExists ? [] :
                await _azureSearchWithHttpApi.GetAllPagesGenericAsync(queryParameters, nameof(StudyLocationSearchView));
            var queryResult = data.DeserializeAzureSearchResults<StudyLocationSearchView>();
            return queryResult;
        }

        public async Task<ResupplyInventoryCounts> GetInventoryCountsForSite(FlashApplicationContext applicationContext, ShipmentRequestSettings shipmentRequestSettings, KitTypeRequest kitTypeRequest, ExpiryLimitView expiryLimits,
            string locationId, string eventId, IEnumerable<KitTypeResponse> kitTypes, CountryDepotPriorityView countryDepotPriorities, List<DistributionGroupResponse> distributionGroups, CancellationToken cancellationToken = default)
        {
            //Available On Site = Expiry Date >= [(Today's date UTC) + (DNI Value) + (Lead Time)]

            var kitType = kitTypes.FirstOrDefault(k => k.Id == kitTypeRequest.Id);
            if (kitType == null)
            {
                return new ResupplyInventoryCounts() { KitTypeId = kitTypeRequest.Id };
            }

            var expiryLimitForKitType = expiryLimits.KitGroups?.Find(k => k.KitTypeIds.Exists(a => a == kitTypeRequest.Id));

            //include leadTime
            var leadTime = await _shipmentService.GetDepotLeadTimeByStudyLocation(locationId, kitType, countryDepotPriorities,
                distributionGroups, applicationContext);
            var minExpiryDate = DateTime.UtcNow.AddDays((expiryLimitForKitType?.DoNotIncludeDays ?? 0) + leadTime);
            var minExpiryDateTimeOffset = new DateTimeOffset(minExpiryDate.Year, minExpiryDate.Month, minExpiryDate.Day, 0, 0, 0, TimeSpan.Zero);

            //determine if "Quarantine" is turned on
            var includeQuarantinedInAvailable = shipmentRequestSettings.CountQuarantinedInventoryAsAvailable ?? false;

            // has the kit counts view been created yet?
            var kitCountsIndexExists = await _azureSearchWithHttpApi.IndexTrackerExists(nameof(KitCountsSearchView), applicationContext.StudySession.SponsorId, applicationContext.StudySession.Environment, cancellationToken);

            //get the aggregated counts based on calculated expiry
            Expression<Func<KitCountsSearchView, bool>> filterExpression =
                d => d.SponsorId == applicationContext.StudySession!.SponsorId &&
                     d.StudyId == applicationContext.StudySession.StudyId &&
                     d.EnvironmentId == applicationContext.StudySession.Environment &&
                     d.KitType == kitType.KitType &&
                     d.KitTypeCode == kitType.KitTypeCode &&
                     d.LocationId == locationId &&
                     d.ExpiryDate != null;
            var filterQuery = QueryExtension.CreateQuery(filterExpression).First();
            var queryParameters = new Dictionary<string, string>();
            queryParameters.AddQueryParametersForAzureSearch(filterQuery);
            var kitCountsData = !kitCountsIndexExists ? [] :
                await _azureSearchWithHttpApi.GetAllPagesGenericAsync(queryParameters, nameof(KitCountsSearchView), applicationContext.StudySession.Environment, applicationContext.StudySession.SponsorId, cancellationToken);
            var queryResult = kitCountsData.DeserializeAzureSearchResults<KitCountsSearchView>();

            //get the aggregated counts based on calculated expiry
            Expression<Func<KitCountsSearchView, bool>> requestedFilterExpression =
                d => d.SponsorId == applicationContext.StudySession!.SponsorId &&
                     d.StudyId == applicationContext.StudySession.StudyId &&
                     d.EnvironmentId == applicationContext.StudySession.Environment &&
                     d.KitType == kitType.KitType &&
                     d.KitTypeCode == kitType.KitTypeCode &&
                     d.LocationId == locationId &&
                     (d.ExpiryDate == null || d.ExpiryDate == DateTimeOffset.MinValue);
            var requestedFilterQuery = QueryExtension.CreateQuery(requestedFilterExpression).First();
            var requestedQueryParameters = new Dictionary<string, string>();
            requestedQueryParameters.AddQueryParametersForAzureSearch(requestedFilterQuery);

            var requestedKitCountsData = !kitCountsIndexExists ? [] :
                await _azureSearchWithHttpApi.GetAllPagesGenericAsync(requestedQueryParameters, nameof(KitCountsSearchView), applicationContext.StudySession.Environment, applicationContext.StudySession.SponsorId, cancellationToken);
            var requestedQueryResult = requestedKitCountsData.DeserializeAzureSearchResults<KitCountsSearchView>();

            //sum the counts
            var availableQuantity = queryResult?.Where(x => x.ExpiryDate >= minExpiryDateTimeOffset).Sum(x => x.Available) ?? 0;
            var quarantinedQuantity = queryResult?.Where(x => x.ExpiryDate >= minExpiryDateTimeOffset).Sum(x => x.Quarantined) ?? 0;
            var inTransitQuantity = queryResult?.Where(x => x.ExpiryDate >= minExpiryDateTimeOffset).Sum(x => x.InTransit) ?? 0;
            var pendingInShipments = queryResult?.Where(x => x.ExpiryDate >= minExpiryDateTimeOffset).Sum(x => x.Pending) ?? 0;
            var pendingInShipmentRequests = requestedQueryResult?.Sum(x => x.Pending) ?? 0;

            var quantity = availableQuantity + inTransitQuantity + pendingInShipments + pendingInShipmentRequests;

            if (includeQuarantinedInAvailable)
                quantity += quarantinedQuantity;

            return new ResupplyInventoryCounts()
            {
                KitTypeId = kitType.Id,
                KitTypeName = kitType.KitType,
                Quantity = quantity,
                AvailableQuantity = queryResult?.GroupBy(x => x.ExpiryDate).Where(x => x.Key != null && x.Sum(y => y.Available) > 0).Select(x => new ResupplyInventoryCountsByExpiry()
                {
                    ExpiryDate = Core.Common.DateHelpers.DateOnlyFromDateTimeOffset(x.Key).Value,
                    Quantity = x.Sum(y => y.Available)
                }).ToList() ?? new List<ResupplyInventoryCountsByExpiry>(),
                QuarantinedQuantity = queryResult?.GroupBy(x => x.ExpiryDate).Where(x => x.Key != null && x.Sum(y => y.Quarantined) > 0).Select(x => new ResupplyInventoryCountsByExpiry()
                {
                    ExpiryDate = Core.Common.DateHelpers.DateOnlyFromDateTimeOffset(x.Key).Value,
                    Quantity = x.Sum(y => y.Available)
                }).ToList() ?? new List<ResupplyInventoryCountsByExpiry>(),
                InTransitQuantity = inTransitQuantity,
                PendingInShipments = pendingInShipments,
                PendingInShipmentRequests = pendingInShipmentRequests
            };
        }

        public async Task<IEnumerable<(string patientId, List<string> dispensationCombinationIds, List<string> dispensationComboGroupdIds, List<ProjectedVisitInfoSearchView> projectedVisits)>> GetPatientProjectedVisitsByLocationAndLookoutWindowLength(string locationId, int lookoutWindowLength,
            ShipmentRequestSettings shipmentSettings, FlashApplicationContext applicationContext, CancellationToken cancellationToken = default)
        {

            var lookoutWindowDatePlusOne = DateTime.UtcNow.AddDays(lookoutWindowLength + 2); // padding with an extra 2 days for the query. Will limit further for each patient based on VisitDateOffset below
            var maxDateForSearchQuery = new DateTimeOffset(lookoutWindowDatePlusOne.Year, lookoutWindowDatePlusOne.Month, lookoutWindowDatePlusOne.Day, 0, 0, 0, TimeSpan.Zero);

            var filterString = $"{nameof(PatientProjectedVisitsSearchView.ProjectedVisits).ToCamelCase()}/any(p: (p/{nameof(ProjectedVisitInfoSearchView.MinExpectedDate).ToCamelCase()} ne null and p/{nameof(ProjectedVisitInfoSearchView.MinExpectedDate).ToCamelCase()} ne {DateTimeOffset.MinValue.ToUniversalTime():o} and p/{nameof(ProjectedVisitInfoSearchView.MinExpectedDate).ToCamelCase()} le {maxDateForSearchQuery.UtcDateTime:o}) or ((p/{nameof(ProjectedVisitInfoSearchView.MinExpectedDate).ToCamelCase()} eq null or p/{nameof(ProjectedVisitInfoSearchView.MinExpectedDate).ToCamelCase()} eq {DateTimeOffset.MinValue.ToUniversalTime():o}) and p/{nameof(ProjectedVisitInfoSearchView.ExpectedDate).ToCamelCase()} le {maxDateForSearchQuery.UtcDateTime:o})) and {nameof(PatientProjectedVisitsSearchView.LocationId).ToCamelCase()} eq '{locationId}'";
            var filterQuery = new Dictionary<string, string> {{ Constants.OpenApi.Name.Filter, filterString }};

            // has the PatientProjectedVisitsSearchView been created yet?
            var indexExists = await _azureSearchWithHttpApi.IndexTrackerExists(nameof(PatientProjectedVisitsSearchView), applicationContext.StudySession.SponsorId,applicationContext.StudySession.Environment, cancellationToken);

            var projectedVisitsData = !indexExists ? [] :
                await _azureSearchWithHttpApi.GetAllPagesGenericAsync(filterQuery, nameof(PatientProjectedVisitsSearchView), applicationContext.StudySession.Environment, applicationContext.StudySession.SponsorId, cancellationToken);
            var searchResponse = projectedVisitsData.DeserializeAzureSearchResults<PatientProjectedVisitsSearchView>();

            //filter
            var response = searchResponse.Select(s =>
            {
                s.ProjectedVisits = s.ProjectedVisits.Where(a =>
                {
                    var isVisitEligible = true;
                    if (a.Eligibility?.Type == EligibilityType.Status.ToString() || a.Eligibility?.Type == ((int)EligibilityType.Status).ToString()) // due to changes made to serialiation of this value in cog search, match on number value or enum name
                    {
                        isVisitEligible = a.Eligibility?.EligibleStatus?.Contains(s.Status) ?? false;
                    }

                    return isVisitEligible;
                }).ToList();
                return s;
            })
            .GroupBy(grp => grp.PatientId)
            .Select(vgp => {
                var projectedVisits = vgp.SelectMany(y => {

                    // limit to visits in the lookout window
                    y.ProjectedVisits = y.ProjectedVisits.Where(a => {
                        var maxVisitDateOnly = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(-a.VisitDateOffset)).DateTime.AddDays(lookoutWindowLength));
                        var minVisitDateOnly = DateOnly.FromDateTime((a.MinExpectedDate ?? a.ExpectedDate).DateTime);
                        return  minVisitDateOnly <= maxVisitDateOnly;
                    }).ToList();

                    var overdueVisits = y.ProjectedVisits.Where(a =>
                    {
                        var minVisitDateOnlyToday = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(-a.VisitDateOffset)).DateTime);
                        var expectedVisitDateOnly = DateOnly.FromDateTime((a.MaxExpectedDate ?? a.ExpectedDate).DateTime);
                        return expectedVisitDateOnly < minVisitDateOnlyToday;
                    }).ToList();

                    // account for whether to include overdue visits
                    if (!(shipmentSettings.IncludeOverdueVisits ?? false))
                    {
                        y.ProjectedVisits = y.ProjectedVisits.Except(overdueVisits).ToList();
                    }
                    else if ((shipmentSettings.StopAfterDaysOverDue ?? false) && shipmentSettings.StopAfterDaysOverDueNumberOfDays.HasValue)
                    {
                        var daysOverdueVisits = overdueVisits.Where(a => {
                            var minVisitDateOnlyTodayPlusDaysOverdue = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(-a.VisitDateOffset)).DateTime).AddDays(-shipmentSettings.StopAfterDaysOverDueNumberOfDays.Value);
                            var expectedVisitDateOnly = DateOnly.FromDateTime((a.MaxExpectedDate ?? a.ExpectedDate).DateTime);
                            return expectedVisitDateOnly < minVisitDateOnlyTodayPlusDaysOverdue;
                        }).ToList();
                        y.ProjectedVisits = y.ProjectedVisits.Except(daysOverdueVisits).ToList();
                    }

                    // limit to the number of visits to lookout for per patient
                    y.ProjectedVisits = (shipmentSettings.LookoutForUpcomingVisits ?? false) && shipmentSettings.LookoutForUpcomingVisitsNumberOfVisits.HasValue ? y.ProjectedVisits.Take(-shipmentSettings.LookoutForUpcomingVisitsNumberOfVisits.Value).ToList() : y.ProjectedVisits;

                    return y.ProjectedVisits;
                }).ToList();
                return (patientId: vgp.Key, vgp.First().DispensationCombinationIds, vgp.First().DispensationComboGroupIds, projectedVisits);
            })
            .Where(a => a.projectedVisits.Any());

            return response;
        }

        public async Task<List<VisitResponse>> GetVisitListAsync(IEnumerable<string> visitIds, FlashApplicationContext applicationContext)
        {
            var studyDesignAgDomain = new StudyDesignDomain();
            await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, applicationContext, _settings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.StudyVisits }, studyDesignAgDomain, skipBehavior: true, bypassCache: true);

            return visitIds.Any()
            ? [.. studyDesignAgDomain.StudyVisits.Where(w => visitIds.Contains(w.Id))]
            : [.. studyDesignAgDomain.StudyVisits];
        }

        public Task<StudyShipmentSettings> GetStudyShipmentSettings(FlashApplicationContext applicationContext)
        {
            return _shipmentService.GetStudyShipmentSettings(applicationContext);
        }

        public async Task LogState(ResupplyTriggerRequest resupplyTriggerRequest, FlashApplicationContext applicationContext)
        {
            if (string.IsNullOrWhiteSpace(_settings.StateLogStorageConnectionString)) return;

            var stateLogs = GetStateLogs(resupplyTriggerRequest, applicationContext);

            var container = _resupplyStateBlobServiceClient.GetBlobContainerClient(_settings.ResupplyStateBlob);
            await container.CreateIfNotExistsAsync();

            // get the append blob client
            var appendBlobClient = container.GetAppendBlobClient($"sponsor={applicationContext.StudySession!.SponsorId}/study={applicationContext.StudySession.StudyId}/environment={applicationContext.StudySession!.Environment}/resupplyState.jsonl");
            await appendBlobClient.CreateIfNotExistsAsync();

            var bytes = stateLogs.SelectMany(inventoryState =>
            {
                // Serialize the inventoryState object to JSON
                var json = JsonConvert.SerializeObject(inventoryState, SerializerSettings.GetJsonSerializerSettings());

                // Convert the JSON string to bytes
                return Encoding.UTF8.GetBytes(json + Environment.NewLine);

            }).ToArray();
            // Write the bytes to the append blob
            if (bytes.Length > 0)
            {
                using var stream = new MemoryStream(bytes);
                await appendBlobClient.AppendBlockAsync(stream);
            }
        }

        public async Task<int> CalculateResupplyNeed(string locationId, int lookoutDays,
            string kitTypeId, ShipmentRequestSettings shipmentSettings, List<VisitResponse> allVisits, List<VisitDispensationComboGroupResponse> dispensationComboGroups, List<VisitDispensationComboResponse> dispensationCombos,  FlashApplicationContext applicationContext, CancellationToken cancellationToken = default)
        {
            //get all patient visits within window
            var projectedVisitsByPatient = await GetPatientProjectedVisitsByLocationAndLookoutWindowLength(locationId, lookoutDays, shipmentSettings, applicationContext, cancellationToken); 

            //for each patient projected visit
            var visitQuantities = projectedVisitsByPatient.Select(patientProjectedVisit =>
            {
                var (_, dispensationComboIds, dispensationComboGroupIds, projectedVisits) = patientProjectedVisit;

                var projectedVisitIds = projectedVisits.Select(v => v.VisitId).Distinct();
                var visitConfigs = allVisits.Where(v => projectedVisitIds.Contains(v.Id)).ToList();
                //titration
                var qty = 0; //initialize the amount

                var resupplyDispensationComboGroups = dispensationComboGroups.Where(x => dispensationComboGroupIds.Contains(x.Id))
                    .Join(dispensationCombos.Where(x => dispensationComboIds.Contains(x.Id)),
                        dcg => dcg.Id,
                        dc => dc.VisitDispensationComboGroupId,
                        (dcg, dc) => new { dcg.Id, DispensationCombination = dc })
                    .GroupBy(dcgGrp => dcgGrp.Id)
                .ToDictionary(dcgId => dcgId.Key, dcg =>
                {
                    return new ResupplyDispensationCombinationGroup()
                    {
                        DispensationCombination = dcg.FirstOrDefault(x => dispensationComboIds.Contains(x.DispensationCombination.Id)).DispensationCombination,
                        IsPredictable = true // set the initial chain state to predictable
                    };
                });

                foreach (var visit in projectedVisits)
                {
                    // try to predict dosage at each visit
                    PredictVisitDosage(visit, visitConfigs, dispensationCombos, kitTypeId, resupplyDispensationComboGroups, ref qty);
                }

                return qty;
            });

            //return sum of quantities for all patients projected visits within window
            return visitQuantities.Sum();
        }

        public async Task<ResupplyStatusViewModel> GetAutoResupplyStatus(FlashApplicationContext applicationContext, Dictionary<string, string> customDimensions, string eventId)
        {
            var dataRepo = await _resupplyStatusDataRepositoryManager.GetTenantDataRepositoryAsync(
                applicationContext.StudySession!.SponsorId!, applicationContext.StudySession.Environment!, eventId);
            var pk = applicationContext.StudySession.GetPartition<ResupplyStatusViewModel>();
            var id = nameof(ResupplyStatusViewModel);
            var response = await dataRepo.TryGetAsync(id, pk);
            if (response != null) return response;

            var status = new ResupplyStatusViewModel()
            {
                SponsorId = applicationContext.StudySession.SponsorId!,
                StudyId = applicationContext.StudySession.StudyId!,
                StudyVersionId = applicationContext.StudySession.StudyVersion!,
                EnvironmentId = applicationContext.StudySession.Environment!
            };

            await dataRepo.UpdateAsync(status, eventId, applicationContext.CurrentUser, customDimensions);
            return status;
        }

        public async Task SetAutoResupplyStatus(bool resupplyStatus, FlashApplicationContext applicationContext, Dictionary<string, string> customDimensions,
            string eventId)
        {
            var dataRepo = await _resupplyStatusDataRepositoryManager.GetTenantDataRepositoryAsync(
                applicationContext.StudySession!.SponsorId!, applicationContext.StudySession.Environment!, eventId);

            var resupplyStatusModel = new ResupplyStatusViewModel()
            {
                SponsorId = applicationContext.StudySession.SponsorId!,
                StudyId = applicationContext.StudySession.StudyId!,
                EnvironmentId = applicationContext.StudySession.Environment!,
                StudyVersionId = applicationContext.StudySession.StudyVersion!,
                AutoResupplyInProgress = resupplyStatus,
                TimeToLive = 2 * 60 * 60 // if there is some problem that prevents the document from being updated to false, it will expire in 2 hours
            };

            await dataRepo.UpdateAsync(resupplyStatusModel, applicationContext.StudySession.GetPartition<ResupplyStatusViewModel>(), applicationContext.CurrentUser, customDimensions, ignoreEtag: true);
        }

        private static void PredictVisitDosage(ProjectedVisitInfoSearchView visit, List<VisitResponse> visitConfigs, List<VisitDispensationComboResponse> dispensationCombos, string kitTypeId, Dictionary<string, ResupplyDispensationCombinationGroup> dispensationCombinationGroups, ref int qty)
        {
            //check the dosing logic to determine if predictable
            foreach (var dcg in dispensationCombinationGroups)
            {
                var dcgDispensationCombos = dispensationCombos.Where(x => x.VisitDispensationComboGroupId == dcg.Key)
                    .OrderBy(x => x.DoseLevel).ToList();

                // if this DCG has been flagged as not predictable in a previous visit then conitnue using the last predictable dose)
                if (dcg.Value.IsPredictable)
                {
                    // get config for visit
                    var visitConfig = visitConfigs.Find(v => v.Id == visit.VisitId);

                    //get the dosing logic for visit
                    var dosingLogic = visitConfig?.DosingLogic?.Find(d => d.VisitDispensationComboGroupId == dcg.Key);

                    if (dosingLogic != null)
                    {
                        switch (dosingLogic.Type)
                        {
                            case DosingLogicType.FixedDispensation:
                                // keep the current dosage
                                break;
                            case DosingLogicType.FlexibleDosingLogic:
                                PredictNextFlexibleDose(dosingLogic, dcg.Value, dcgDispensationCombos);
                                break;
                            case DosingLogicType.StepDosingLogic:
                                PredictNextStepDose(dosingLogic, dcg.Value, dcgDispensationCombos);
                                break;
                            case DosingLogicType.InvestigatorsChoice:
                                dcg.Value.IsPredictable = false;
                                //keep the current dosage
                                break;
                        }
                    }
                }

                //get the visit sets that contain the visit id
                var dispensationVisitSets = dcg.Value.DispensationCombination.VisitSets?
                    .Where(w => w.VisitIds.Contains(visit.VisitId)).ToList();

                //get the qty from the dosage pair based on where we find our kit type in the visit sets
                qty += dispensationVisitSets?.Where(w => w.KitTypeQtyPair.ContainsKey(kitTypeId))
                    .SelectMany(s => s.KitTypeQtyPair)
                    .Where(w => w.Key == kitTypeId)
                    .Sum(s => s.Value) ?? 0;
            }
        }

        private static void PredictNextFlexibleDose(VisitDosingLogic dosingLogic, ResupplyDispensationCombinationGroup dcg, List<VisitDispensationComboResponse> visitDispensationCombos)
        {
            var currentFlexibleDosingLogic = dosingLogic?.DosingRules?.Find(d => d.PreviousDispensationCombinationId == dcg.DispensationCombination.Id);
            if (currentFlexibleDosingLogic == null || currentFlexibleDosingLogic.AvailableDispensationCombinationIds.Count > 1)
            {
                dcg.IsPredictable = false;
            }
            else
            {
                //replace the current dispensation combo id based on known titration
                var dcId = currentFlexibleDosingLogic.AvailableDispensationCombinationIds[0];
                dcg.DispensationCombination = visitDispensationCombos.Find(x => x.Id == dcId);
            }
        }

        private static void PredictNextStepDose(VisitDosingLogic dosingLogic, ResupplyDispensationCombinationGroup dcg, List<VisitDispensationComboResponse> visitDispensationCombos)
        {
            if (dosingLogic.DosingRules.Count > 1)
            {
                dcg.IsPredictable = false;
            }
            else
            {
                var steps = dosingLogic.DosingRules[0].NumberOfSteps;
                var direction = dosingLogic.DosingRules[0].Step;

                var patientCurrentDispensationComboIndex =
                    visitDispensationCombos.FindIndex(x => dcg.DispensationCombination.Id == x.Id);

                //determine direction to look
                switch (direction)
                {
                    case StepDosingType.StepUp:
                        patientCurrentDispensationComboIndex += steps;
                        break;
                    case StepDosingType.StepDown:
                        patientCurrentDispensationComboIndex -= steps;
                        break;
                    case StepDosingType.NoOption:
                    default:
                        break;
                }

                //reaplce the current dispensation combo id based on known titration
                dcg.DispensationCombination = visitDispensationCombos[patientCurrentDispensationComboIndex];
            }
        }

        private static List<ResupplyInventoryState> GetStateLogs(ResupplyTriggerRequest resupplyTriggerRequest, FlashApplicationContext appContext)
        {
            var timeStamp = DateTimeOffset.UtcNow;
            return resupplyTriggerRequest.ResupplyInformationList.Select(x =>

                new ResupplyInventoryState()
                {
                    StudyData = new ResupplyInventoryStateStudyData()
                    {
                        SponsorId = appContext.StudySession!.SponsorId!,
                        StudyId = appContext.StudySession.StudyId!,
                        DateTimeUtc = timeStamp
                    },
                    DestinationLocation = new ResupplyInventoryStateDestinationLocation()
                    {
                        LocationId = x.LocationId,
                        LocationName = x.LocationName,
                        Country = x.LocationCountry,
                        Inventory = x.ResupplyStateInfo.Inventory.ToList(),
                        Shipments = x.ResupplyStateInfo.Shipments.ToList(),
                        ShipmentRequests = x.ResupplyStateInfo.ShipmentRequests.ToList(),
                        ResupplyNeeds = x.ResupplyStateInfo.ResupplyNeeds.ToList()
                    },
                    SupplySetting = new ResupplyInventoryStateSupplySetting()
                    {
                        SupplySettingId = x.SupplySettingId,
                        SupplySettingName = x.SupplySettingName,
                        KitTypes = x.KitGroups.SelectMany(kg => kg.KitTypes, (kg, kt) => new ResupplyInventoryStateSupplySettingKitType()
                        {
                            KitTypeId = kt.Id,
                            KitTypeName = kt.Name,
                            MinimumThresholdQuantity = kg.MinimumThresholdQuantity,
                            MaximumThresholdQuantity = kg.MaximumThresholdQuantity,
                            LookoutWindow = kg.LookoutWindowLength,
                            ResupplyWindow = kg.ResupplyWindowLength
                        }).ToList()
                    }
            }).ToList();
        }
    }

    public class ResupplyDispensationCombinationGroup
    {
        public VisitDispensationComboResponse DispensationCombination { get; set; } = null!;
        public bool IsPredictable { get; set; } = true;
    }

    public class ResupplyInventoryCounts
    {
        public string KitTypeId { get; set; } = null!;
        public string KitTypeName { get; set; } = null!;
        public int Quantity { get; set; }
        public List<ResupplyInventoryCountsByExpiry> AvailableQuantity { get; set; }
        public List<ResupplyInventoryCountsByExpiry> QuarantinedQuantity { get; set; }
        public int InTransitQuantity { get; set; }
        public int PendingInShipments { get; set; }
        public int PendingInShipmentRequests { get; set; }
    }

    public class ResupplyInventoryCountsByExpiry
    {
        public DateOnly ExpiryDate { get; set; }
        public int Quantity { get; set; }
    }
}
