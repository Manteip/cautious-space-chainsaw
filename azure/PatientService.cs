using Azure.Core;
using Endpoint.Flash.Core.Authorization;
using Endpoint.Flash.Core.Authorization.Domain;
using Endpoint.Flash.Core.Extensions.AzureSearch.SearchTenantManager;
using Endpoint.Flash.Core.Extensions.CosmosDb.Generic;
using Endpoint.Flash.Core.Extensions.CosmosDb.Generic.DataRepository.RepoManager;
using Endpoint.Flash.Core.Runtime.Domain.Inventory;
using Endpoint.Flash.Core.Services.Domain;
using Endpoint.Flash.Core.Services.Interface;
using Endpoint.Flash.FlowBuilder.Domain.Survey;
using Endpoint.Flash.Patients.Runtime.Domain.Patient.Model;
using Endpoint.Flash.Patients.Runtime.Domain.Visit;
using Endpoint.Flash.Patients.Runtime.Domain.Visit.Model;
using Endpoint.Flash.PatientsDomain.VisitSchedule;
using Endpoint.Flash.PatientsDomain.VisitSchedule.Model;
using Endpoint.Flash.StudySettings.Domain;
using Endpoint.Flash.StudySettings.Domain.Treatment;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Extensions;
using System.Linq.Expressions;
using System.Threading;
using static Endpoint.Flash.Core.Design.Domain.AppConstant;
using DispensationType = Endpoint.Flash.Patients.Runtime.Domain.Patient.Model.DispensationType;

namespace Endpoint.Flash.Patients.Runtime.Functions.Patient.Services;
#nullable enable

public partial class PatientService(IMediator mediator,
    ApplicationSettings appSettings,
    ILogger<PatientService> logger,
    IDataRepositoryManager<ManuallyEnteredVisitModel> manuallyEnteredVisitModelRepoManager,
    IDataRepositoryManager<PatientDispensationModel> patientDispensationRepositoryManager,
    IDataRepositoryManager<PatientVisitModel> patientVisitRepositoryManager,
    IDataRepositoryManager<PatientSummaryModel> patientSummaryDataRepositoryManager,
    IDataRepositoryManager<PatientSummarySearchViewModel> patientSummarySearchViewModelDataRepoManager,
    IDataRepositoryManager<PatientModel> patientDataRepositoryManager,
    IDataRepositoryManager<PatientUnblindModel> patientUnblindDataRepositoryManager,
    IDataRepositoryManager<PatientVisitWindowApprovalModel> patientVisitWindowApprovalDataRepositoryManager,
    IDataRepositoryManager<PatientProjectedVisitsModel> patientProjectedVisitsDataRepositoryManager,
    IAzureSearchManager<PatientSummarySearchView> patientSummarySearchManager,
    IStudyService studyService,
    ILocationLookupService locationLookupService,
    IUserRolePermissionService userRolePermissionService,
    IMapper mapper) : IPatientService
{
    private readonly IMediator _mediator = mediator;
    private readonly ApplicationSettings _appSettings = appSettings;
    private readonly ILogger<PatientService> _logger = logger;
    private readonly IDataRepositoryManager<ManuallyEnteredVisitModel> _manuallyEnteredVisitModelRepoManager = manuallyEnteredVisitModelRepoManager;
    private readonly IDataRepositoryManager<PatientDispensationModel> _patientDispensationRepositoryManager = patientDispensationRepositoryManager;
    private readonly IDataRepositoryManager<PatientVisitModel> _patientVisitRepositoryManager = patientVisitRepositoryManager;
    private readonly IDataRepositoryManager<PatientSummaryModel> _patientSummaryDataRepositoryManager = patientSummaryDataRepositoryManager;
    private readonly IDataRepositoryManager<PatientSummarySearchViewModel> _patientSummarySearchViewModelDataRepoManager = patientSummarySearchViewModelDataRepoManager;
    private readonly IDataRepositoryManager<PatientModel> _patientDataRepositoryManager = patientDataRepositoryManager;
    private readonly IDataRepositoryManager<PatientUnblindModel> _patientUnblindDataRepositoryManager = patientUnblindDataRepositoryManager;
    private readonly IAzureSearchManager<PatientSummarySearchView> _patientSummarySearchManager = patientSummarySearchManager;
    private readonly IStudyService _studyService = studyService;
    private readonly ILocationLookupService _locationLookupService = locationLookupService;
    private readonly IUserRolePermissionService _userRolePermissionService = userRolePermissionService;
    private readonly IDataRepositoryManager<PatientVisitWindowApprovalModel> _patientVisitWindowApprovalDataRepositoryManager = patientVisitWindowApprovalDataRepositoryManager;
    private readonly IDataRepositoryManager<PatientProjectedVisitsModel> _patientProjectedVisitsDataRepositoryManager = patientProjectedVisitsDataRepositoryManager;
    private readonly IMapper _mapper = mapper;

    public async Task<string> GetScreeningScheduleId(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.VisitSchedules], studyDesignAgDomain, skipBehavior: true);

        return studyDesignAgDomain.VisitSchedules.Where(x => x.Phase == Patients.Domain.AppConstant.VisitScheduleScreeningPhase).Select(y => y.Id).FirstOrDefault()!;
    }

    public async Task<Dictionary<string, string>> GetFirstScreeningVisitInfo(string visitScheduleId, FlashApplicationContext appContext)
    {
        var additionalQueryFields = new Dictionary<string, string> {
            { nameof(VisitModel.VisitScheduleId), visitScheduleId }
        };
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.StudyVisits], studyDesignAgDomain, skipBehavior: true, additionalQueryFields: additionalQueryFields);
        return studyDesignAgDomain.StudyVisits.Where(x => x.VisitOrder == "1" && x.Type == VisitType.Scheduled.ToString()).Select(y => new Dictionary<string, string> { { y.Id!, y.Name } }).FirstOrDefault()!;
    }

    public async Task<List<VisitResponse>> GetScheduleVisits(string visitScheduleId, FlashApplicationContext appContext)
    {
        Dictionary<string, string> additionalQueryFields = string.IsNullOrWhiteSpace(visitScheduleId) ? new() :
            new()  {
                { nameof(VisitModel.VisitScheduleId), visitScheduleId }
            };
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.StudyVisits], studyDesignAgDomain, skipBehavior: true, additionalQueryFields: additionalQueryFields);
        return studyDesignAgDomain.StudyVisits;
    }

    public async Task<VisitResponse> GetSingleVisitById(string visitId, FlashApplicationContext appContext)
    {
        var additionalQueryFields = new Dictionary<string, string> {
            { nameof(VisitModel.Id), visitId }
        };
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.StudyVisits], studyDesignAgDomain, skipBehavior: true, additionalQueryFields: additionalQueryFields);
        return studyDesignAgDomain.StudyVisits.FirstOrDefault()!;
    }

    public async Task<List<VisitResponse>> GetStudyVisits(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.StudyVisits], studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.StudyVisits;
    }

    public async Task<bool> ScheduleAndVisitExists(FlashApplicationContext appContext)
    {
        bool exists = true;
        var visitScheduleId = await GetScreeningScheduleId(appContext);
        if (!string.IsNullOrWhiteSpace(visitScheduleId))
        {
            var firstVisit = await GetFirstScreeningVisitInfo(visitScheduleId, appContext);
            exists = !firstVisit.IsNullOrDefault();
        }
        else
        {
            exists = false;
        }
        return exists;
    }

    public async Task<List<VisitScheduleResponse>> GetVisitSchedules(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.VisitSchedules], studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.VisitSchedules;
    }

    public async Task<VisitScheduleResponse?> GetVisitScheduleById(string visitScheduleId, FlashApplicationContext appContext)
    {
        return (await GetVisitSchedules(appContext)).FirstOrDefault(x => x.Id == visitScheduleId);
    }

    public async Task<List<TreatmentResponse>> GetTreatments(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.StudyTreatments], studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.StudyTreatments;
    }

    public async Task<string> GetStudyName(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, [AppConstants.Entity.Design.StudySettings], studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.StudySettings!.StudyName!;
    }

    public async Task<List<PatientVisitModel>> GetPatientVisitsForPatient(FlashApplicationContext appContext, string patientId, string visitId = null!, CancellationToken cancellationToken = default)
    {
        var patientVisitsDataRepo = await _patientVisitRepositoryManager.GetTenantDataRepositoryAsync(
            appContext.StudySession!.SponsorId!,
            appContext.StudySession.Environment!,
            cancellationToken: cancellationToken);

        Expression<Func<PatientVisitModel, bool>> patientVisitExpr = string.IsNullOrWhiteSpace(visitId) ?
            x =>
            x.IsActive &&
            x.PatientId == patientId :
            x =>
            x.IsActive &&
            x.PatientId == patientId &&
            x.VisitId == visitId;

        var patientVisits = await patientVisitsDataRepo.GetAsync(patientVisitExpr, appContext.StudySession.GetPartition<PatientVisitModel>());

        return patientVisits.ToList();
    }

    public async Task<bool> GetPatientVisitsForPatientExists(FlashApplicationContext appContext, string patientId, string visitId = null!)
    {
        var patientVisitsDataRepo = await _patientVisitRepositoryManager.GetTenantDataRepositoryAsync(
            appContext.StudySession!.SponsorId!,
            appContext.StudySession.Environment!);

        Expression<Func<PatientVisitModel, bool>> patientVisitExpr = string.IsNullOrWhiteSpace(visitId) ?
            x =>
            x.IsActive &&
            x.PatientId == patientId :
            x =>
            x.IsActive &&
            x.PatientId == patientId &&
            x.VisitId == visitId;

        return await patientVisitsDataRepo.ExistsAsync(patientVisitExpr, appContext.StudySession.GetPartition<PatientVisitModel>());
    }

    public async Task<PatientVisitModel?> GetPatientVisitByPatientVisitId(FlashApplicationContext appContext, string patientVisitId)
    {
        var patientVisitRepo = await _patientVisitRepositoryManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!);
        var pkPatientVisit = appContext.StudySession.GetPartition<PatientVisitModel>();
        var patientVisitModel = await patientVisitRepo.TryGetAsync(patientVisitId, pkPatientVisit).ConfigureAwait(false);
        return patientVisitModel;
    }

    public async Task<PatientSummaryModel?> GetPatientSummary(string patientId, FlashApplicationContext appContext, CancellationToken cancellationToken = default)
    {
        var summaryRepository = await _patientSummaryDataRepositoryManager.GetTenantDataRepositoryAsync(
                appContext.StudySession!.SponsorId!,
                appContext.StudySession.Environment!,
                cancellationToken: cancellationToken);

        PatientSummaryModel? patientSummary;
        if (Guid.TryParse(patientId, out _))
        {
            patientSummary = await summaryRepository.TryGetAsync(
                patientId!,
                appContext.StudySession!.GetPartition<PatientSummaryModel>(),
                cancellationToken);
        }
        else
        {
            Expression<Func<PatientSummaryModel, bool>> expr =
                d => d.PatientId == patientId! &&
                d.IsActive;
            var items = await summaryRepository.GetAsync(expr,
                appContext.StudySession!.GetPartition<PatientSummaryModel>(),
                cancellationToken);
            patientSummary = items.FirstOrDefault();
        }
        return patientSummary;
    }

    private async Task<List<PatientVisitModel>> GetPatientCompletedVisitDates(string patientId, FlashApplicationContext appContext)
    {
        var currentDataRepo =
            await _patientVisitRepositoryManager.GetTenantDataRepositoryAsync(appContext.StudySession?.SponsorId!,
                           appContext.StudySession?.Environment!);

        Expression<Func<PatientVisitModel, bool>> expr =
            d => d.PatientId == patientId &&
                 d.Status == VisitStatus.Completed &&
                 d.IsActive;

        var results = await currentDataRepo.GetQuery(appContext.StudySession!.GetPartition<PatientVisitModel>(), expr)
            .Select(a => new { a.VisitId, a.VisitDate, a.Occurrence, a.CycleOccurrence }).AsAsyncQueryable().ToListAsync();

        return results?.Select(x => new PatientVisitModel { VisitId = x.VisitId, VisitDate = x.VisitDate, Occurrence = x.Occurrence, CycleOccurrence = x.CycleOccurrence }).ToList() ?? [];
    }

    public async Task<IEnumerable<PatientDispensationModel>> CreateDispensationModelForInventoryReplacement(string patientVisitId, List<InventoryTrackerModel> dispensedInventory, List<InventoryTrackerModel> replacedInventory,
        FlashApplicationContext appContext, Dictionary<string, string> customDimensions, string eventId, KitTypeResponse kitTypeResponse, string patientDispensationModelGuid)
    {
        //lookup the patientDispensationModel by patientVisitId
        var currentDataRepo =
            await _patientDispensationRepositoryManager.GetTenantDataRepositoryAsync(appContext.StudySession?.SponsorId!,
                appContext.StudySession?.Environment!, eventId);

        Expression<Func<PatientDispensationModel, bool>> expr =
            d => d.PatientVisitId == patientVisitId &&
                 d.DispensationType == DispensationType.Visit &&
                 d.IsActive;

        //get the current patientDispensationModel
        var results =
            await currentDataRepo.GetAsync(expr, appContext.StudySession!.GetPartition<PatientDispensationModel>());
        var patientDispensationModel = results.FirstOrDefault();

        //set this to a replacement type
        patientDispensationModel!.DispensationType = DispensationType.Replacement;

        //setup the inventory replaced
        patientDispensationModel.Dispensation.InventoryReplaced = replacedInventory.Select(s => new InventoryItem()
        {
            BatchNumber = s.BatchNumber!,
            ExpiryDate = string.IsNullOrWhiteSpace(s.ExpiryDate) ? DateOnly.MinValue! : DateHelpers.DateOnlyFromDateString(s.ExpiryDate)!.Value,
            InventoryTrackerId = s.Id,
            KitManagementType = ((KitManagementType)s.KitManagementType).GetDescription(),
            KitNumber = s.KitNumber!,
            KitType = s.KitType!,
            KitTypeId = kitTypeResponse.Id,
            LotNumber = s.LotNumber!,
            ManufacturingLotNumbers = s.ManufacturingLotNumbers,
            Quantity = s.Quantity,
            VendorLotNumber = s.VendorLotNumber!
        }).ToList();

        //setup the dispensed inventory
        patientDispensationModel.Dispensation.InventoryDispensed = dispensedInventory.Select(s => new InventoryItem()
        {
            BatchNumber = s.BatchNumber!,
            ExpiryDate = string.IsNullOrWhiteSpace(s.ExpiryDate) ? DateOnly.MinValue! : DateHelpers.DateOnlyFromDateString(s.ExpiryDate)!.Value,
            InventoryTrackerId = s.Id,
            KitManagementType = ((KitManagementType)s.KitManagementType).GetDescription(),
            KitNumber = s.KitNumber!,
            KitType = s.KitType!,
            KitTypeId = kitTypeResponse.Id,
            LotNumber = s.LotNumber!,
            ManufacturingLotNumbers = s.ManufacturingLotNumbers,
            Quantity = s.Quantity,
            VendorLotNumber = s.VendorLotNumber!
        }).ToList();

        //remove the unique identifiers before create
        patientDispensationModel.Etag = null!;
        patientDispensationModel.Id = patientDispensationModelGuid;

        var modelsToCreate = new List<PatientDispensationModel>()
        {
            patientDispensationModel
        };
        //create a new patientDispensationModel with new values
        return await currentDataRepo.CreateAsync(modelsToCreate, eventId!, appContext.CurrentUser, customDimensions);

    }

    public async Task<PatientDispensationModel> CreateDispensationModelForManualDispensation(DateOnly? dispensationDate, string manuallyEnteredVisitName, string patientId, string patientVisitId, string visitName, List<InventoryTrackerModel> dispensedInventory,
        FlashApplicationContext appContext, Dictionary<string, string> customDimensions, string eventId, KitTypeResponse kitTypeResponse)
    {

        // if visitName is passed but not patientVisitId or visitId, then it is a manually entered visit in runtime but not in design
        if (!string.IsNullOrWhiteSpace(manuallyEnteredVisitName))
        {
            var manuallyEnteredVisitModelRepo = await _manuallyEnteredVisitModelRepoManager.GetTenantDataRepositoryAsync(appContext.StudySession!.SponsorId!, appContext.StudySession.Environment!, eventId);
            var manuallyEnteredVisit = new ManuallyEnteredVisitModel()
            {
                Id = manuallyEnteredVisitName,
                CorrelationId = manuallyEnteredVisitName,
                Name = manuallyEnteredVisitName,
                StudyId = appContext.StudySession!.StudyId!,
                StudyVersionId = appContext.StudySession.StudyVersion!,
                EnvironmentId = appContext.StudySession.Environment!,
                SponsorId = appContext.StudySession.SponsorId!
            };
            _ = await manuallyEnteredVisitModelRepo.UpdateAsync(manuallyEnteredVisit, eventId, appContext.CurrentUser, customDimensions, ignoreEtag: true);
        }

        //lookup the patientDispensationModel by patientVisitId
        var patientDispensationRepo =
            await _patientDispensationRepositoryManager.GetTenantDataRepositoryAsync(appContext.StudySession?.SponsorId!,
                appContext.StudySession?.Environment!, eventId);

        //set this to a replacement type
        var patientDispensationModel = new PatientDispensationModel()
        {
            StudyId = appContext.StudySession!.StudyId!,
            StudyVersionId = appContext.StudySession.StudyVersion!,
            SponsorId = appContext.StudySession.SponsorId!,
            EnvironmentId = appContext.StudySession.Environment!,
            DispensationDate = dispensationDate,
            DispensationType = DispensationType.Manual,
            PatientVisitId = patientVisitId,
            VisitName = visitName,
            PatientId = patientId,
            Dispensation = new Dispensation()
            {
                InventoryDispensed = dispensedInventory?.Select(s => new InventoryItem()
                {
                    BatchNumber = s.BatchNumber!,
                    ExpiryDate = string.IsNullOrWhiteSpace(s.ExpiryDate) ? DateOnly.MinValue! : DateHelpers.DateOnlyFromDateString(s.ExpiryDate)!.Value,
                    InventoryTrackerId = s.Id,
                    KitManagementType = ((KitManagementType)s.KitManagementType).GetDescription(),
                    KitNumber = s.KitNumber!,
                    KitType = s.KitType!,
                    KitTypeId = kitTypeResponse.Id,
                    LotNumber = s.LotNumber!,
                    ManufacturingLotNumbers = s.ManufacturingLotNumbers,
                    Quantity = s.Quantity,
                    VendorLotNumber = s.VendorLotNumber!
                }).ToList() ?? []
            }
        };

        //create a new patientDispensationModel with new values
        return await patientDispensationRepo.CreateAsync(patientDispensationModel, eventId!, appContext.CurrentUser, customDimensions);
    }

    public async Task<List<VisitDispensationComboResponse>> GetVisitDispensationComboResponses(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.DispensationCombos }, studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.DispensationCombos;
    }

    public async Task<List<CohortConfig>> GetCohortConfigs(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.CohortCofig }, studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.CohortConfig;
    }

    public async Task<List<VisitDispensationComboGroupResponse>> GetVisitDispensationComboGroupsResponses(FlashApplicationContext appContext)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.DispensationComboGroups }, studyDesignAgDomain, skipBehavior: true);
        return studyDesignAgDomain.DispensationComboGroups;
    }

    public async Task<StudyBaseResponse> GetStudyInfoAsync(StudySession studySession)
    {
        var studyVersion = studySession.StudyVersion;
        var environmentId = (!string.IsNullOrWhiteSpace(studySession.Environment)) ? studySession.Environment : FlashConstant.UserEnvironment.Development.Id;
        var studyId = studySession.StudyId;

        var mostRecentVersion = await _studyService.GetMostRecentStudyVersion(studySession.SponsorId!, studyId!, _appSettings);
        StudyStatusDetail studyStatusDetail;

        if (!string.IsNullOrWhiteSpace(studyVersion))
        {
            var studyVersions = await _studyService.GetStudyStatus(studySession.SponsorId!, studyId!, _appSettings);
            studyStatusDetail = studyVersions.First(x => x.StudyVersionId == studyVersion &&
                (
                    (environmentId == FlashConstant.UserEnvironment.Development.Id && (x.Status.Development.State == StudyState.Active || x.Status.Development.State == StudyState.Deployed)) ||
                    (environmentId == FlashConstant.UserEnvironment.Testing.Id && (x.Status.Testing.State == StudyState.Active || x.Status.Testing.State == StudyState.Deployed)) ||
                    (environmentId == FlashConstant.UserEnvironment.UserAcceptance.Id && (x.Status.UserAcceptance.State == StudyState.Active || x.Status.UserAcceptance.State == StudyState.Deployed)) ||
                    (environmentId == FlashConstant.UserEnvironment.Production.Id && (x.Status.Production.State == StudyState.Active || x.Status.Production.State == StudyState.Deployed))
                )
            );
        }
        else
        {
            studyStatusDetail = await _studyService.GetMostRecentStudyVersionInEnv(studySession.SponsorId!, studyId!, environmentId!, _appSettings);
        }

        var response = new StudyBaseResponse
        {
            StudyId = studyStatusDetail!.StudyId,
            StudyName = studyStatusDetail.StudyName,
            StudyVersion = studyStatusDetail.Version.ToString(),
            StudyVersionId = studyStatusDetail.StudyVersionId,
            SponsorId = studyStatusDetail.SponsorId,
            SponsorName = studyStatusDetail.SponsorName,
            SponsorLogo = studyStatusDetail.SponsorLogo,
            IsLatestVersion = mostRecentVersion.StudyVersionId == studyStatusDetail.StudyVersionId,
            Id = studyStatusDetail.StudyVersionId,
            ProgramId = studyStatusDetail.ProgramId,
            IsStudyEditable = !studyStatusDetail.IsLocked,
        };

        return response;
    }

    public async Task<bool> IsDrugSafetyUser(ApplicationContext context, string userId = null!)
    {
        // lookup user permissions
        var permissions = await GetUserPermissions(context, userId);

        return permissions?.OperationPermissions.Any(op => op.PermissionSection == PermissionSection.StudyRunTime && (op.Permissions?.Any(opr => opr.Value?.Any(oprv => oprv == Domain.AppConstant.OperationTags.DrugSafetyUnblindPatient) ?? false) ?? false)) ?? false;
    }

    public void ValidateAgeRange(AgeCheckType ageCheckType,
        PatientPiiDto pii,
        bool checkAgeBorder,
        bool checkSoftWindow,
        ApplicationContext appContext,
        PatientNumberDomain patientSettings,
        List<CountryPiiSetting> piiSettings,
        Dictionary<string, dynamic> inputs)
    {
        bool hardAgeOutOfRange = false;
        bool showAgeBorderWarning = false;
        bool showSoftWindowWarning = false;

        var currentDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(-appContext.CurrentUser.UtcOffset));

        var dobCheckType = GetDobCollectionType(piiSettings);
        var ageSettings = piiSettings.SingleOrDefault(a => a.Id == Domain.AppConstant.PiiFieldIds.AgeField);
        var checkAgeEnabled = ageCheckType switch
        {
            AgeCheckType.AddPatient => patientSettings.AgeRange.CheckAgeAtAddPatient,
            AgeCheckType.FirstVisit => patientSettings.AgeRange.CheckAgeAtFirstVisit,
            _ => false
        };

        var hardStopEnabled = ageCheckType switch
        {
            AgeCheckType.AddPatient => patientSettings.AgeRange.CheckAgeAtAddPatientHardStop,
            AgeCheckType.FirstVisit => patientSettings.AgeRange.CheckAgeAtFirstVisitHardStop,
            _ => false
        };

        if (((ageSettings?.Value ?? false) || !string.IsNullOrWhiteSpace(dobCheckType)) && checkAgeEnabled)
        {
            // only use Dob/MYob/Yob if age is not collected
            var age = (ageSettings?.Value ?? false) ?
                (pii.Age ?? 0) :
                pii.DateOfBirth.CalculateAge(dobCheckType, currentDate);

            var borderWarning = (ageSettings?.Value ?? false) ? false :
                dobCheckType switch
                {
                    // don't show border warning when collecting full DOB. Any time of the day is valid for birthday
                    { } s when s == Domain.AppConstant.PiiFieldIds.MyobField => (currentDate.Year - patientSettings.AgeRange.MinValue == pii.DateOfBirth?.Year && currentDate.Month == pii.DateOfBirth?.Month) ||
                                                                            (currentDate.Year - patientSettings.AgeRange.MaxValue - 1 == pii.DateOfBirth?.Year && currentDate.Month == pii.DateOfBirth?.Month),
                    { } s when s == Domain.AppConstant.PiiFieldIds.YobField => currentDate.Year - patientSettings.AgeRange.MinValue == pii.DateOfBirth?.Year ||
                                                                            currentDate.Year - patientSettings.AgeRange.MaxValue - 1 == pii.DateOfBirth?.Year,
                    _ => false,
                };

            if (age < patientSettings.AgeRange.MinValue || age > patientSettings.AgeRange.MaxValue || borderWarning)
            {
                if (hardStopEnabled)
                {
                    if (checkAgeBorder && borderWarning)
                    {
                        showAgeBorderWarning = true;
                    }
                    else if (!borderWarning)
                    {
                        hardAgeOutOfRange = true;
                    }
                }
                else if (checkSoftWindow)
                {
                    showSoftWindowWarning = true;
                }
            }
        }

        inputs["HardAgeOutOfRange"] = hardAgeOutOfRange;
        inputs["ShowAgeBorderWarning"] = showAgeBorderWarning;
        inputs["ShowSoftWindowWarning"] = showSoftWindowWarning;
    }

    public async Task<PatientModel> GetPatientPiiAsync(FlashApplicationContext appContext, string patientId, CancellationToken cancellationToken)
    {
        var patientPiiRepository =
          await _patientDataRepositoryManager.GetTenantDataRepositoryAsync(
                appContext.StudySession!.SponsorId!,
                appContext.StudySession.Environment!,
                cancellationToken: cancellationToken);

        PatientModel patientPii;
        if (Guid.TryParse(patientId, out _))
        {
            patientPii = (await patientPiiRepository.TryGetAsync(
                patientId!,
                appContext.StudySession!.GetPartition<PatientModel>(),
                cancellationToken))!;
        }
        else
        {
            Expression<Func<PatientModel, bool>> expr =
                d => d.PatientId == patientId! &&
                d.IsActive;
            var items = await patientPiiRepository.GetAsync(expr,
                appContext.StudySession!.GetPartition<PatientModel>(),
                cancellationToken);
            patientPii = items.FirstOrDefault()!;
        }

        return patientPii;
    }

    public async Task<PatientNumberDomain> GetPatientSettings(string countryCode, ApplicationContext context)
    {
        //get the pii settings model for the country selected
        var config = new StudyDesignDomain { };
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, context, _appSettings, new List<string> { AppConstants.Entity.Design.PatientNumber }, config, countryCode, true);
        return config.PatientNumber;
    }

    public async Task<IEnumerable<CountryPiiSetting>> GetPiiSettings(string countryCode, ApplicationContext context)
    {
        //get the pii settings model for the country selected
        var config = new StudyDesignDomain { };
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, context, _appSettings,
            new List<string> { AppConstants.Entity.Design.CountriesPii }, config, countryCode, true);
        return config.CountriesPii?.SingleOrDefault()?.PiiSettings ?? new List<CountryPiiSetting> { };
    }

    public static string GetDobCollectionType(List<CountryPiiSetting> piiSettings)
    {
        var dobSetting = piiSettings.SingleOrDefault(a => a.Id == Domain.AppConstant.PiiFieldIds.DateOfBirthField);
        if (dobSetting?.Value ?? false) //Is Dob PiiSetting
        {
            var activeDobSetting = dobSetting.Children.Find(x => x.Value);
            if (activeDobSetting != null)
            {
                return activeDobSetting.Id!;
            }
        }
        return string.Empty;
    }

    public async Task<PatientVisitWindowApprovalModel> GetOrCreatePatientOutOfWindow(string patientId, string visitId, int occurrence, int cycleOccurrence, FlashApplicationContext appContext, Dictionary<string, string> customDimensions, string eventId)
    {
        PatientVisitWindowApprovalModel patientVisitWindowApprovalModel;
        var currentDataRepo =
            await _patientVisitWindowApprovalDataRepositoryManager.GetTenantDataRepositoryAsync(appContext.StudySession?.SponsorId!, appContext.StudySession?.Environment!);

        Expression<Func<PatientVisitWindowApprovalModel, bool>>
            expr = p => p.VisitId == visitId &&
                        p.Occurrence == occurrence &&
                        p.CycleOccurrence == cycleOccurrence &&
                        p.IsActive;

        var partitionKey = PartitionExtension.GetPartition<PatientVisitWindowApprovalModel>(appContext.StudySession!.StudyId!, appContext.StudySession!.Environment!, patientId);

        var currentOutOfWindowExists = await currentDataRepo.ExistsAsync(expr, partitionKey);

        if (!currentOutOfWindowExists)
        {
            patientVisitWindowApprovalModel = await currentDataRepo.CreateAsync(
            new PatientVisitWindowApprovalModel
            {
                PatientId = patientId,
                VisitId = visitId,
                Occurrence = occurrence,
                CycleOccurrence = cycleOccurrence,
                StudyId = appContext.StudySession.StudyId!,
                EnvironmentId = appContext.StudySession.Environment!,
                StudyVersionId = appContext.StudySession.StudyVersion!,
                SponsorId = appContext.StudySession.SponsorId!
            }
            , eventId, appContext.CurrentUser, customDimensions);
        }
        else
        {
            var result = await currentDataRepo.GetAsync(expr, partitionKey);
            patientVisitWindowApprovalModel = result.FirstOrDefault()!;

            if (patientVisitWindowApprovalModel.ApprovalStatus == VisitApprovalStatus.Denied)
            {
                patientVisitWindowApprovalModel.ApprovalStatus = null;
                await currentDataRepo.UpdateAsync(patientVisitWindowApprovalModel, eventId, appContext.CurrentUser, customDimensions);
            }
        }

        return patientVisitWindowApprovalModel;
    }

    public async Task<bool> IsUnBlindedUser(ApplicationContext context, string userId = null!)
    {
        // lookup user permissions
        var permissions = await GetUserPermissions(context, userId);

        return (permissions?.DataPermissions.TryGetValue(DataPermission.AccessToUnblindedInformation, out var unblinded) ?? false) && unblinded;
    }

    private async Task<UserRolePermissionModel> GetUserPermissions(ApplicationContext context, string userId = null!)
    {
        var previewSession = string.IsNullOrWhiteSpace(userId) ? context.CurrentUser.CurrentPreviewSession : null;
        var isSuperAdminUser = context.CurrentUser.IsSuperAdmin(
               context.StudySession!.Environment!,
               context.CurrentUser.CurrentPreviewSession!);
        var permissions = await _userRolePermissionService.GetUserPermissionAsync(
            isSuperAdminUser,
            string.IsNullOrWhiteSpace(userId) ? (context.CurrentUser.UserId ?? string.Empty) : userId,
            context.StudySession?.StudyId ?? string.Empty,
            context.StudySession?.SponsorId ?? string.Empty,
            context.SessionId ?? string.Empty,
            PermissionSection.System,
            context.CurrentUser,
            studyVersionId: context.StudySession?.StudyVersion ?? string.Empty,
            envrionmentId: context.StudySession?.Environment ?? string.Empty,
            previewSession: previewSession!,
            readOnly: false);

        return permissions!;
    }

    public async Task<List<StudySettingElementConfig>> GetStudyElementsAsync(FlashApplicationContext appContext)
    {
        var studyDesignDomain = new StudyDesignDomain();
        await StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _appSettings,
                           new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.StudyElements }, studyDesignDomain, skipBehavior: true, bypassCache: true);

        return studyDesignDomain.StudyElements;
    }

    public async Task<long> GetUniquePatientId(string sponsorId, string environmentId, string eventId)
    {
        var randomGenerator = new RandomGenerator();
        long randomPatientId = randomGenerator.GetLong(1, long.MaxValue);
        var patientExists = await IsPatientIdUnique(randomPatientId, sponsorId, environmentId, eventId);

        if (patientExists)
        {
            return await GetUniquePatientId(sponsorId, environmentId, eventId); // try again
        }

        return randomPatientId;
    }

    private async Task<PatientProjectedVisitsModel> CreateProjectedVisitsForNewPatient(PatientSummarySearchViewModel summaryModel, PatientSummaryModel patientSummary, ApplicationContext appContext)
    {
        var projectedVisitsModel = new PatientProjectedVisitsModel
        {
            StudyId = summaryModel.StudyId,
            StudyVersionId = summaryModel.StudyVersionId,
            SponsorId = summaryModel.SponsorId,
            PatientId = summaryModel.PatientId,
            VisitScheduleId = summaryModel.VisitScheduleId,
            RandId = summaryModel.RandId,
            CohortId = summaryModel.CohortId,
            TreatmentId = summaryModel.TreatmentId,
            EnvironmentId = summaryModel.EnvironmentId,
            Status = summaryModel.Status,
            LocationId = summaryModel.SiteId!
        };
        return await BuildProjectedVisits(projectedVisitsModel, patientSummary, null!, (FlashApplicationContext)appContext, null!);
    }

    private async Task<bool> IsPatientIdUnique(long patientId, string sponsorId, string environmentId, string eventId)
    {
        var queryParams = new Dictionary<string, string>() { { Core.Common.Constants.OpenApi.Name.Filter, $"{nameof(PatientSummarySearchView.PatientId).ToCamelCase()} eq '{patientId}'" } };
        var azureSearchClient = await _patientSummarySearchManager.GetAzureSearchClient(sponsorId, environmentId, eventId);

        // send request to cognitive search
        var queryResult = await azureSearchClient.GetItemsAsync(queryParams).ConfigureAwait(false);

        var exists = queryResult.Count > 0;
        return exists;
    }

    private async Task LookupSiteInformation(PatientSummarySearchViewModel model, ApplicationContext appContext)
    {

        var location = await _locationLookupService.LookupLocationByIdAsync(model.SiteId, appContext);

        if (location != null)
        {
            model.SiteName = location.LocationName ?? model.SiteName;
            model.SiteNumber = location.LocationNumber ?? model.SiteNumber;
        }
    }

    private async Task LookupVisitInfo(PatientSummarySearchViewModel model, ApplicationContext appContext)
    {
        var screeingScheduleId = await GetScreeningScheduleId((FlashApplicationContext)appContext);
        model.VisitScheduleId = screeingScheduleId;
        var screeningVisitInfo = await GetFirstScreeningVisitInfo(model.VisitScheduleId, (FlashApplicationContext)appContext);

        var upcomingVisit = screeningVisitInfo.FirstOrDefault();
        var upcomingVisitId = upcomingVisit.IsNullOrDefault() ? null : screeningVisitInfo.Select(x => x.Key).First();
        var upcomingVisitName = upcomingVisit.IsNullOrDefault() ? null : screeningVisitInfo.Select(x => x.Value).First();

        model.UpcomingVisitDate = upcomingVisit.IsNullOrDefault() ? null : appContext.CurrentUser.GetUserSessionDateTimeOffset();
        model.UpcomingVisitId = upcomingVisitId;
        model.UpcomingVisitName = upcomingVisitName;
    }
}

//testing //
