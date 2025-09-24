using Endpoint.Flash.Patients.Runtime.Domain.Patient.Model;
using Endpoint.Flash.Aggregator.Domain.Cots;
using Endpoint.Flash.Aggregator.Domain.Design;
using Endpoint.Flash.Core.Authorization.Domain;
using Endpoint.Flash.Core.Common.EventGridClientPublisher;
using Endpoint.Flash.Core.Design.Domain.DataDictionary;
using Endpoint.Flash.Core.Design.Domain.DataRestriction;
using Endpoint.Flash.Core.Extensions.AzureSearch;
using Endpoint.Flash.Core.Extensions.AzureSearch.Interface;
using Endpoint.Flash.Core.Extensions.CosmosDb.Generic.DatatRepository;
using Endpoint.Flash.Core.Runtime.Domain.DataChanges;
using Endpoint.Flash.Core.Runtime.Domain.FormBuilder;
using Endpoint.Flash.Core.Runtime.Domain.ProductReturns;
using Endpoint.Flash.Core.Services.MapTerms;
using Endpoint.Flash.FlowBuilder.Domain.Action.Index;
using Endpoint.Flash.FlowBuilder.Domain.Action.Model;
using Endpoint.Flash.FlowBuilder.Domain.FormBuilder.Model;
using Endpoint.Flash.InventoryManagement.Domain.Inventory;
using Endpoint.Flash.InventoryManagement.Service;
using Endpoint.Flash.PatientsDomain.VisitSchedule;
using Endpoint.Flash.Shipment.Domain.Shipment;
using Endpoint.Flash.StudySettings.Domain.DataChangeGroups;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using DataChangeTypeResponse = Endpoint.Flash.SystemOperations.Runtime.Domain.DataChangeTypeResponse;

namespace Endpoint.Flash.SystemOperations.Runtime.Service;

public class DataChangeService : IDataChangeService
{
    private readonly IDataRepositoryManager<DataChangeTypeModel> _dataChangeTypeConfigRepository;
    private readonly IUserRolePermissionService _userRolePermissionService;
    private readonly IAzureSearchWithHttpApi _httpSearch;
    private readonly IDataChangeScriptBlobServiceResolverService _blobServiceResolverService;
    private readonly ICustomDataChangeScriptBlobServiceResolverService _customActionBlocServiceResolverService;
    private readonly ILocationLookupService _locationLookupService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMediator _mediator;
    private readonly Settings _settings;
    private readonly IMapper _mapper;
    private readonly IMapTermService _mapTermService;
    private readonly ILogger<DataChangeService> _logger;
    private readonly IServiceBusRequestReplyClient _requestReplyClient;


    public DataChangeService(
        IDataRepositoryManager<DataChangeTypeModel> dataChangeTypeConfigRepository,
        IUserRolePermissionService userRolePermissionService,
        IAzureSearchWithHttpApi httpSearch,
        IDataChangeScriptBlobServiceResolverService blobServiceResolverService,
        ICustomDataChangeScriptBlobServiceResolverService customActionBlocServiceResolverService,
        ILocationLookupService locationLookupService,
        IServiceProvider serviceProvider,
        IMediator mediator,
        Settings settings,
        IMapper mapper,
        IMapTermService mapTermService,
        ILogger<DataChangeService> logger,
        IServiceBusRequestReplyClient requestReplyClient)
    {
        _dataChangeTypeConfigRepository = dataChangeTypeConfigRepository;
        _userRolePermissionService = userRolePermissionService;
        _httpSearch = httpSearch;
        _blobServiceResolverService = blobServiceResolverService;
        _customActionBlocServiceResolverService = customActionBlocServiceResolverService;
        _locationLookupService = locationLookupService;
        _serviceProvider = serviceProvider;
        _mediator = mediator;
        _settings = settings;
        _mapper = mapper;
        _mapTermService = mapTermService;
        _logger = logger;
        _requestReplyClient = requestReplyClient;
    }

    public async Task<List<DataChangeCustomParamResponse>> GetDataChangeCustomParams(string dataChangeTypeId, ApplicationContext appContext, Dictionary<string, string> customDimensions, CancellationToken cancellationToken = default)
    {
        // get data to change from cs script
        List<DataChangeCustomParamResponse> results = new();
        try
        {
            var dataToChangeScript = await LoadDataChangeScript(dataChangeTypeId, appContext, customDimensions, cancellationToken);
            var resultTasks = dataToChangeScript.CustomParameters.Select(async cp => {
                var cpr = _mapper.Map<DataChangeCustomParamResponse>(cp);
                cpr.ListOptions = cp.DataType == DataChangeDataType.List || cp.DataType == DataChangeDataType.MultiSelectList ?
                    await dataToChangeScript.GetCustomParamterListOptions(cp.Id, customDimensions) :
                    new List<KeyValuePair<string, string>>();
                return cpr;
            });

            results = (await Task.WhenAll(resultTasks)).ToList();
        }
        catch
        {
            _logger.LogInformation("Valid Data Change Script not found for Data Change Type Id: {DataChangeTypeId}", dataChangeTypeId);
        }

        return results;
    }

    public async Task<List<DataChangeDataTypeEntity>> GetDataChangeTypeEntities(DataChangeDataTypeEntityRequest request, ApplicationContext appContext, Dictionary<string, string> customDimensions, bool enforceDataChangeGroupPermissions = true, CancellationToken cancellationToken = default)
    {
        // get data to change from cs script
        List<DataChangeDataTypeEntity> results = new();
        try
        {
            var dataToChangeScript = await LoadDataChangeScript(request.DataChangeTypeId, appContext, customDimensions, cancellationToken);
            results = await dataToChangeScript.GetDataChangeTypeEntities(request);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Valid Data Change Script not found for Data Change Type Id: {DataChangeTypeId}", request.DataChangeTypeId);
        }

        if (results.Count > 0)
        {
            var dataChangeGroupsTask = GetDataChangeGroups(appContext, cancellationToken);
            var userPermissionsTask = GetUserPermissionAsync(appContext);
            await Task.WhenAll(dataChangeGroupsTask, userPermissionsTask);
            var dataChangeGroups = await dataChangeGroupsTask;
            var userPermissions = await userPermissionsTask;

            results = results?.Where(x => !x.PermissionsOnly || !enforceDataChangeGroupPermissions).ToList()!; // allow returning entities for use on design permissions page but will not show on runtime

            // filter based on data change group permissions
            return results.FilterDataChangeTypeEntitiesByPermission(userPermissions!, dataChangeGroups, appContext, enforceDataChangeGroupPermissions);
        }

        return results;
    }

    public async Task<List<DataChangeDataTypeEntity>> GetDataChangeTypeEntitiesFromServiceBus(DataChangeDataTypeEntityRequest request, ApplicationContext appContext, Dictionary<string, string> customDimensions, bool enforceDataChangeGroupPermissions = true, CancellationToken cancellationToken = default)
    {
        var sbRequest = new DataChangeDataTypeEntityServiceBusRequest(request, (FlashApplicationContext)appContext, customDimensions, enforceDataChangeGroupPermissions);

        var sessionId = Guid.NewGuid().ToString();
        var queueName = $"{_settings.StampId}-{appContext.StudySession!.Environment}-{AppConstant.GetDataChangeTypeEntitiesRequestName}";
        var response = await _requestReplyClient.Request<List<DataChangeDataTypeEntity>>(queueName, sbRequest, sessionId!, cancellationToken: cancellationToken);

        return response ?? new List<DataChangeDataTypeEntity>();
    }

    public async Task<List<DataChangeGroupResponse>> GetDataChangeGroups(ApplicationContext appContext, CancellationToken cancellationToken = default)
    {
        var studyDesignAgDomain = new StudyDesignDomain();
        await Aggregator.Service.Helper.StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _settings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.DataChangeGroups }, studyDesignAgDomain, skipBehavior: true, bypassCache: true);

        return studyDesignAgDomain.DataChangeGroups;
    }

    public async Task<Dictionary<string, bool>> GetAvailableActionsForUser(DataChangeResponseBase dataChange, ApplicationContext appContext, DataChangeGroupResponse? dataChangeGroup, UserRolePermissionModel? userPermission = null!)
    {
        userPermission ??= (await GetUserPermissionAsync(appContext))!;
        var actions = Enum.GetValues(typeof(DataChangeAction)).Cast<DataChangeAction>()
            .Select(x => new KeyValuePair<string, bool>(x.ToString().ToCamelCase(), false)).ToDictionary(x => x.Key, x => x.Value);

        var dataChangeStep = GetDataChangeActiveStep(dataChange);
        foreach (var action in actions)
        {
            actions[action.Key] = dataChangeStep switch
            {
                DataChangeStep.Created => SetCreatedActionStatus(action, dataChange, dataChangeGroup, userPermission, appContext),
                DataChangeStep.UnderReview => SetUnderReviewActionStatus(action, dataChange, dataChangeGroup, userPermission, appContext),
                DataChangeStep.SecondaryReview => SetSeconaryReviewActionStatus(action, dataChange, dataChangeGroup, userPermission, appContext),
                _ => false
            };
        }
        return actions;
    }

    public DataChangeStep? GetDataChangeActiveStep(DataChangeResponseBase dataChange)
    {
        var lastHistory = dataChange.History?.Where(x => x.Action != DataChangeAction.Edit).OrderByDescending(dco => dco.UpdatedDate).FirstOrDefault();
        if (dataChange.Status.Key == DataChangeStatus.Created.ToString())
        {
            return DataChangeStep.Created;
        }
        else if (dataChange.Status.Key == DataChangeStatus.Submitted.ToString())
        {
            return DataChangeStep.UnderReview;
        }
        else if (dataChange.Status.Key == DataChangeStatus.Approved.ToString() && lastHistory?.Action == DataChangeAction.Approve && lastHistory.Step == DataChangeStep.UnderReview && dataChange.RequiresSecondaryReview)
        {
            return DataChangeStep.SecondaryReview;
        }
        else if (dataChange.Status.Key == DataChangeStatus.Processed.ToString())
        {
            return DataChangeStep.Processed;
        }
        return null;
    }

#pragma warning disable S3776 // cog complexity
    public async Task<string> ValidateDataDictionaryValue(List<string> values, string dataDictionaryId, ApplicationContext appContext)
    {
#pragma warning restore S3776 // cog complexity
        var studyDesignAgDomain = new StudyDesignDomain();
        await Aggregator.Service.Helper.StudyDesignHelper.GetDesignConfigurationAsync(_mediator, appContext, _settings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Design.DataDictionary }, studyDesignAgDomain, skipBehavior: true, bypassCache: true);
        var dataDictionaryConfigs = studyDesignAgDomain.DataDictionary;
        var dataDictionaryConfig = dataDictionaryConfigs.First(x => x.Id == dataDictionaryId);

        if (dataDictionaryConfig == null)
        {
            return "Data dictionary not found";
        }

        if (dataDictionaryConfig.DataType == StudySettings.Domain.AppConstant.DataDictionary.DataTypes.Integer)
        {
            var updatedValue = values.FirstOrDefault()?.ToString();
            if (!int.TryParse(updatedValue, out var integerValue))
            {
                return "Must be an integer";
            }
            return ValidateNumberRange(dataDictionaryConfig, integerValue);

        }
        else if (dataDictionaryConfig.DataType == StudySettings.Domain.AppConstant.DataDictionary.DataTypes.Decimal)
        {
            var updatedValue = values.FirstOrDefault()?.ToString();
            if (!decimal.TryParse(updatedValue, out var decimalValue))
            {
                return "Must be a decimal";
            }
            // Check if the decimal value exceeds the allowed number of decimal places
            var roundedValue = Math.Round(decimalValue, dataDictionaryConfig.DecimalPlaces ?? 0);
            if (decimalValue != roundedValue)
            {
                return $"The entered value must be {dataDictionaryConfig.DecimalPlaces} number of decimal places.";
            }
            return ValidateNumberRange(dataDictionaryConfig, decimalValue);
        }
        else if (dataDictionaryConfig.DataType == StudySettings.Domain.AppConstant.DataDictionary.DataTypes.Boolean)
        {
            var updatedValue = values.FirstOrDefault()?.ToString();
            var validBool = false;
            if (bool.TryParse(updatedValue, out var booleanValue) || (updatedValue?.Equals("yes", StringComparison.OrdinalIgnoreCase) ?? false) || (updatedValue?.Equals("no", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                validBool = true;
            }
            if (!validBool)
            {
                var boolString = dataDictionaryConfig.DataFormat == StudySettings.Domain.AppConstant.DataDictionary.DataFormats.YesNo.Value ? "Yes/No" : "True/False";
                return $"The entered value must be either {boolString}.";
            }
        }
        else if (dataDictionaryConfig.DataType == StudySettings.Domain.AppConstant.DataDictionary.DataTypes.Date)
        {
            var updatedValue = values.FirstOrDefault()?.ToString();
            if (!DateTime.TryParse(updatedValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dateValue))
            {
                return "Please follow the date format of DD-Mmm-YYYY.";
            }
        }
        else if (dataDictionaryConfig.DataType == StudySettings.Domain.AppConstant.DataDictionary.DataTypes.DateAndTime)
        {
            var updatedValue = values.FirstOrDefault()?.ToString();
            if (!DateTime.TryParse(updatedValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dateTimeValue))
            {
                return "Please follow the date format of DD-Mmm-YYYY 00:00.";
            }
        }
        else if (dataDictionaryConfig.DataType == StudySettings.Domain.AppConstant.DataDictionary.DataTypes.List)
        {
            if (dataDictionaryConfig.DataFormat == StudySettings.Domain.AppConstant.DataDictionary.DataFormats.SingleSelectDropdown.Value &&
                (values.Count != 1 || !dataDictionaryConfig.ListItems.Exists(x => x.Value?.Trim() == values[0])))
            {
                    return $"The entered value must be one of the following values: {string.Join(", ", dataDictionaryConfig.ListItems.Select(x => x.Value))}";
            }

            if (dataDictionaryConfig.DataFormat == StudySettings.Domain.AppConstant.DataDictionary.DataFormats.MultiSelect.Value &&
                (values.Count == 0 || !values.TrueForAll(item => dataDictionaryConfig.ListItems.Exists(y => y.Name?.Trim() == item))))
            {
                    return $"The entered value must be one or more of the following values: {string.Join(", ", dataDictionaryConfig.ListItems.Select(x => x.Value))}. Please enter multiple items in a comma separated fashion.";
            }
        }

        return null!;
    }

    public async Task<DataChangeTypeModel> UpdateDataChangeType(string id, string scriptId, string scriptPath, string eventId, ApplicationContext appContext, Dictionary<string, string> customDimensions, CancellationToken cancellationToken = default)
    {
        var dataChangeTypeDataRepo = _dataChangeTypeConfigRepository.GetDataRepository(Core.Common.Constants.ConnectionIds.Universal, cancellationToken: cancellationToken);

        var patchOperations = new List<JsonDiffPatchDotNet.Formatters.JsonPatch.Operation>()
        {
            new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation
            {
                Op = PatchOperationType.Replace.ToString().ToCamelCase(),
                Path = $"/{nameof(DataChangeTypeModel.ScriptId).ToCamelCase()}",
                Value = scriptId
            },
            new JsonDiffPatchDotNet.Formatters.JsonPatch.Operation
            {
                Op = PatchOperationType.Replace.ToString().ToCamelCase(),
                Path = $"/{nameof(DataChangeTypeModel.ScriptPath).ToCamelCase()}",
                Value = scriptPath
            }
        };

        return await dataChangeTypeDataRepo.PatchAsync(
            id: id,
            patchOperations: patchOperations
                .PatchToCosmosPatchOperation(_settings.CosmosInfo!.ComsosSystemKeys),
            partitionKeyValue: nameof(DataChangeModel),
            eventId: eventId,
            etag: "*",
            userSession: appContext.CurrentUser,
            customDimensions: customDimensions,
            cancellationToken: cancellationToken);
    }

    public async Task<DataChangeTypeModel> UpsertDataChangeType(DataChangeTypeModel dataChangeTypeModel, string eventId, ApplicationContext appContext, Dictionary<string, string> customDimensions, CancellationToken cancellationToken = default)
    {
        var dataChangeTypeDataRepo = _dataChangeTypeConfigRepository.GetDataRepository(Core.Common.Constants.ConnectionIds.Universal, cancellationToken: cancellationToken);

        (dataChangeTypeModel, _) = await dataChangeTypeDataRepo.UpdateAsync(
            dataChangeTypeModel,
            eventId,
            appContext.CurrentUser,
            customDimensions,
            cancellationToken: cancellationToken);

        return dataChangeTypeModel;
    }

    public async Task<List<DataChangeTypeResponse>> GetDataChangeTypes(ApplicationContext appContext, bool enforceDataChangeGroupPermissions = true, bool returnOnlyApprovedCustomActions = true, CancellationToken cancellationToken = default)
    {
        var dataChangeGroupsTask = GetDataChangeGroups(appContext, cancellationToken);
        var userPermissionsTask = GetUserPermissionAsync(appContext);
        await Task.WhenAll(dataChangeGroupsTask, userPermissionsTask);
        var dataChangeGroups = await dataChangeGroupsTask;
        var userPermissions = await userPermissionsTask;
        var cotsAgDomain = new CotsDomain();
        await Aggregator.Service.Helper.CotsHelper.GetCotsDataAsync(_mediator, appContext, _settings, new List<string>() { Aggregator.Domain.AppConstants.Entity.Cots.DataChangeTypes, Aggregator.Domain.AppConstants.Entity.Cots.ActionTypes }, cotsAgDomain, skipBehavior: true, bypassCache: true);

        var stockDataChanges = cotsAgDomain.DataChangeTypes?
            .Where(x => x.IsStock).Select(x => _mapper.Map<DataChangeTypeResponse>(x)).ToList()
            .FilterDataChangeTypesByPermission(userPermissions!, dataChangeGroups, appContext, enforceDataChangeGroupPermissions);
        var customDataChanges = cotsAgDomain.DataChangeTypes?.Where(x => !x.IsStock).Select(x => _mapper.Map<DataChangeTypeResponse>(x)).ToList();
        var queryParams = new Dictionary<string, string>() {{ Core.Common.Constants.OpenApi.Name.Filter, $"search.in({nameof(CustomActionSearchView.Id).ToCamelCase()}, '{string.Join(",", customDataChanges?.Select(x => x.Id) ?? [])}', ',')" }};
        var genericItems = await _httpSearch.GetAllPagesGenericAsync(queryParams, nameof(CustomActionSearchView), cancellationToken: cancellationToken);
        var customActionTypes = genericItems.DeserializeAzureSearchResults<CustomActionSearchView>();

        customDataChanges = cotsAgDomain.DataChangeTypes?
            .Where(x => !x.IsStock)
            .Join(customActionTypes, x => x.Id, y => y.Id,
                (dct, cat) => new { dct, cat })
            .Where(x =>
                (!returnOnlyApprovedCustomActions || x.cat.Status.Key == ActionStatus.Approved.ToString() || appContext.StudySession?.Environment == FlashConstant.UserEnvironment.Development.Id) &&
                x.cat.AssociatedStudies.Exists(y => y.SponsorId == appContext.StudySession!.SponsorId && y.StudyIds.Exists(sid => sid == appContext.StudySession!.StudyId)))
            .GroupBy(x => x.dct.Id)
            .Select(g => g.OrderByDescending(x => x.cat.VersionNumber).First())
            .Select(x => new DataChangeTypeResponse
            {
                Id = x.dct.Id,
                IsStock = x.dct.IsStock,
                Name = x.dct.Name,
                ActionType = UpdateForTermMapping(x.cat.ActionType!),
                RecordIdentification = x.dct.RecordIdentification?.Where(rid => !string.IsNullOrWhiteSpace(rid)).ToList() ?? [],
                ScriptId = x.dct.ScriptId,
                ScriptName = x.dct.ScriptName,
                ScriptPath = x.dct.ScriptPath
            }).ToList()
            .FilterDataChangeTypesByPermission(userPermissions!, dataChangeGroups, appContext, enforceDataChangeGroupPermissions);

        customDataChanges = customDataChanges?.Count > 0 ? (await Task.WhenAll(customDataChanges!.Select(cdc => _mapTermService.MapStudyMapTerms(appContext.StudySession!, cdc)))).ToList() : [];
        return (stockDataChanges ?? []).Union(customDataChanges ?? [])
            .OrderBy(x => x.ActionType).ThenBy(x => x.Name).ToList()!;
    }

    public async Task<ShipmentSearchView?> GetShipmentById(string shipmentId, ApplicationContext context)
    {
        Expression<Func<ShipmentSearchView, bool>> expr =
            d => d.SponsorId == context.StudySession!.SponsorId
                 && d.StudyId == context.StudySession.StudyId
                 && d.EnvironmentId == context.StudySession.Environment
                 && d.Id == shipmentId;

        var query = QueryExtension.CreateQuery(expr);
        query[Core.Common.Constants.OpenApi.Name.Size] = "1";

        var searchOutput = await _httpSearch.GetItemsGenericAsync(query, nameof(ShipmentSearchView),
            context.StudySession!.Environment!,
            context.StudySession.SponsorId!);

        return searchOutput.Results.DeserializeAzureSearchResults<ShipmentSearchView>().FirstOrDefault();
    }

    public async Task<DataChangeRequestModel> FillOutAdditionalParams(DataChangeRequestModel dataChangeRequestModel, ApplicationContext appContext, Dictionary<string, string> customDimensions, CancellationToken cancellationToken = default)
    {
        if ((dataChangeRequestModel.AdditionalParams?.Count ?? 0) == 0) return dataChangeRequestModel;

        var paramConfigs = await GetDataChangeCustomParams(dataChangeRequestModel.DataChangeTypeId, appContext, customDimensions, cancellationToken);
        foreach (var param in dataChangeRequestModel.AdditionalParams!)
        {
            var paramConfig = paramConfigs.Find(x => x.Id == param.ParamId);
            param.DataType = paramConfig?.DataType ?? DataChangeDataType.String;
            param.ListOptions = paramConfig?.ListOptions ?? new List<KeyValuePair<string, string>>();
            param.Name = paramConfig?.Name ?? string.Empty;
        }

        return dataChangeRequestModel;
    }

    public async Task<IDataChangeEntities> LoadDataChangeScript(string dataChangeTypeId, ApplicationContext appContext, Dictionary<string, string> customDimensions, CancellationToken cancellationToken = default)
    {
        var dataChangeTypes = await GetDataChangeTypes(appContext, false, true, cancellationToken);
        var dataChangeType = dataChangeTypes.FirstOrDefault(x => x.Id == dataChangeTypeId);
        if (dataChangeType == null)
        {
            throw new InvalidOperationException($"DataChange with id '{dataChangeTypeId}' not found");
        }

        var blobService = string.IsNullOrWhiteSpace(dataChangeType.ScriptId) ?
            _blobServiceResolverService.GetBlobService(_settings) :
            _customActionBlocServiceResolverService.GetBlobService((ApplicationSettings)_settings);

        // Read the script code from the file
        string code;

        if (string.IsNullOrWhiteSpace(dataChangeType.ScriptId))
        {
            code = await blobService.GetFileTextAsync($"{_settings.DataChangeScriptOptions.ScriptPath}/{dataChangeType.ScriptName}", _settings.DataChangeScriptOptions.BlobStorageContainer);
        }
        else
        {
            var queryParams = new Dictionary<string, string>() {{ Core.Common.Constants.OpenApi.Name.Filter, $"{nameof(CustomActionSearchView.Id).ToCamelCase()} eq '{dataChangeType.ScriptId}'" }};
            var genericItems = await _httpSearch.GetAllPagesGenericAsync(queryParams, nameof(CustomActionSearchView), cancellationToken: cancellationToken);
            var customActionTypes = genericItems.DeserializeAzureSearchResults<CustomActionSearchView>();

            var customActionType =
                customActionTypes
                .Where(x =>
                (x.Status.Key == ActionStatus.Approved.ToString() || appContext.StudySession?.Environment == FlashConstant.UserEnvironment.Development.Id) &&
                x.AssociatedStudies.Exists(y => y.SponsorId == appContext.StudySession!.SponsorId && y.StudyIds.Exists(sid => sid == appContext.StudySession!.StudyId)))
                .GroupBy(x => x.Id)
                .Select(g => g.OrderByDescending(x => x.VersionNumber).First())
                .First();
            var customDataChangeScriptPath = $"Id={customActionType.Id}/Version={customActionType.Version}/Status={customActionType.Status.Key}/{customActionType.Id}.csx";
            var customActionContainer = customActionType.Status.Key == ActionStatus.Approved.ToString() || customActionType.Status.Key == ActionStatus.Published.ToString() ?
                ((ApplicationSettings)_settings).CustomActionScriptContainer :
                ((ApplicationSettings)_settings).CustomActionReferenceFileContainer;
            code = await blobService.GetFileTextAsync(customDataChangeScriptPath, customActionContainer);
        }

        // Create a dictionary of global variables that will be available to the script
        dynamic globals = new ScriptGlobals() {
            Globals = new Dictionary<string, object>()
            {
                { "_logger", _logger },
                { "_appContext", appContext },
                { "_customDimensions", customDimensions },
                { "_serviceProvider", _serviceProvider }
            }
        };

        //add reference to needed interfaces in rand script
        var scriptOptions = Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default.AddReferences(
            typeof(IDataTransactionService).Assembly,
            typeof(InventoryTrackerModel).Assembly,
            typeof(IInventoryService).Assembly,
            typeof(PatientDispensationModel).Assembly,
            typeof(Newtonsoft.Json.Linq.JArray).Assembly,
            typeof(Endpoint.FormBuilder.InternalModel.ElementBase).Assembly,
            typeof(FormBuilderModel).Assembly,
            typeof(IDataChangeEntities).Assembly,
            typeof(FlashApplicationContext).Assembly,
            typeof(DataRepositoryManager<>).Assembly,
            typeof(DataRepository<>).Assembly,
            typeof(IDataRepositoryManager<>).Assembly,
            typeof(Core.Common.Constants.ConnectionIds).Assembly,
            typeof(PatientVisitModel).Assembly,
            typeof(System.Threading.Tasks.Task).Assembly,
            typeof(SurveyResultModel).Assembly,
            typeof(DataDictionaryViewModel).Assembly,
            typeof(DataRestrictionConfigModel).Assembly,
            typeof(IMapper).Assembly,
            typeof(DataChangeDataTypeEntity).Assembly,
            typeof(DataChangeTypeModel).Assembly,
            typeof(ValidationErrorModel).Assembly,
            typeof(PatientSummaryModel).Assembly,
            typeof(CommitTransactionActivity).Assembly,
            typeof(Endpoint.Flash.Patients.Runtime.Domain.Patient.PatientSummaryModel).Assembly,
            typeof(Microsoft.Azure.Cosmos.CosmosClient).Assembly,
            typeof(GenericEvent<>).Assembly,
            typeof(IEventGridPublisher).Assembly,
            typeof(EventGridExtension).Assembly,
            typeof(Azure.Core.Request).Assembly,
            typeof(Settings).Assembly,
            typeof(IAggregatorLookupService).Assembly,
            typeof(VisitType).Assembly,
            typeof(ILogger).Assembly
            );
        //prepend usings to the script needed for assembly references
        code = @$"using Endpoint.Flash.SystemOperations.Runtime.Service;
            using Microsoft.Extensions.Logging;
            using Endpoint.Flash.Core.Extensions.Function.DataTransaction;
            using Endpoint.Flash.Core.Runtime.Domain.Inventory;
            using Endpoint.Flash.InventoryManagement.Service;
            using Endpoint.Flash.Patients.Runtime.Domain.Patient.Model;
            using Newtonsoft.Json.Linq;
            using Endpoint.FormBuilder.InternalModel;
            using Endpoint.Flash.FlowBuilder.Domain.FormBuilder.Model;
            using Endpoint.Flash.PatientsDomain.VisitSchedule;
            using Endpoint.Flash.Core.Extensions.Mediator.Extension;
            using Endpoint.Flash.Core.Extensions.Mediator.Base;
            using Endpoint.Flash.Patients.Runtime.Domain.Patient;
            using Endpoint.Flash.Core.Domain.Interface;
            using Microsoft.Azure.Cosmos;
            using Endpoint.Flash.Core.Common.Extension;
            using Endpoint.Flash.Core.Extensions.AzureSearch.SearchTenantManager;
            using Endpoint.Flash.Core.Common.FlashException.Domain;
            using Endpoint.Flash.Core.Runtime.Domain.DataChanges;
            using Endpoint.Flash.SystemOperations.Runtime.Domain;
            using Endpoint.Flash.Core.Design.Domain.DataRestriction;
            using Endpoint.Flash.FlowBuilder.Runtime.Domain.FormBuilder;
            using Endpoint.Flash.StudySettings.Domain.DataDictionary;
            using Endpoint.Flash.Patients.Runtime.Domain.Visit;
            using Endpoint.Flash.SystemOperations.Runtime.Domain;
            using Endpoint.Flash.Core.Domain.Context;
            using Endpoint.Flash.Core.Extensions.CosmosDb.Generic.DataRepository.RepoManager;
            using Endpoint.Flash.Core.Common;
            using Endpoint.Flash.Core.Domain;
            using Endpoint.Flash.Core.Runtime.Domain;
            using AutoMapper;
            using System;
            using System.Linq;
            using System.Linq.Expressions;
            using System.Threading.Tasks;
            using System.Collections.Generic;
            using Endpoint.Flash.Core.Common.EventGridClientPublisher;
            using AppConstant = Endpoint.Flash.SystemOperations.Runtime.Domain.AppConstant;
            using Newtonsoft.Json;

            {code}";
        // Compile and run the script
        // The script should return a class instance that implements IDataChangeEntities
        ScriptState<object> scriptState = await CSharpScript.RunAsync(code, globals: globals, options: scriptOptions, cancellationToken: cancellationToken);

        // Get the IDataChangeEntities instance from the script
        IDataChangeEntities entities = scriptState.ReturnValue as IDataChangeEntities ?? throw new InvalidCastException($"The script did not return an {nameof(IDataChangeEntities)} instance");
        return entities;
    }

    public class ScriptGlobals
    {
        public Dictionary<string, object> Globals { get; set; } = new Dictionary<string, object>();
    }

    public async Task<CustomActionSearchView?> GetCustomAction(string id, ApplicationContext appContext,
        CancellationToken cancellationToken)
    {
        var queryParams = new Dictionary<string, string>()
        {
            {
                Core.Common.Constants.OpenApi.Name.Filter,
                $"{nameof(CustomActionSearchView.Id).ToCamelCase()} eq '{id}'"
            }
        };
        var genericItems = await _httpSearch.GetAllPagesGenericAsync(queryParams, nameof(CustomActionSearchView),
            cancellationToken: cancellationToken);
        var customDataChangeActions = genericItems.DeserializeAzureSearchResults<CustomActionSearchView>();

        return customDataChangeActions
            .Where(x =>
                x.Status.Key == ActionStatus.Approved.ToString() &&
                x.AssociatedStudies.Exists(y =>
                    y.SponsorId == appContext.StudySession!.SponsorId &&
                    y.StudyIds.Exists(sid => sid == appContext.StudySession!.StudyId)))
            .GroupBy(x => x.Id)
            .Select(g => g.OrderByDescending(x => x.VersionNumber).First())
            .FirstOrDefault();
    }

    public async Task<List<DataChangeInventory>> GetInventory(DataChangeModel dataChange, ApplicationContext appContext,
        CancellationToken cancellationToken)
    {
        var result = new List<DataChangeInventory>();
        if ((dataChange?.InventoryTrackerIds?.Count ?? 0) > 0)
        {
            var locationResult = await _locationLookupService.LookupLocationOrDepotWithLocationTypeByIdAsync(dataChange!.LocationId!, appContext);
            if (locationResult.locationType != Flash.Core.Common.Constants.LocationType.Depot)
            {
                var invQueryParams = new Dictionary<string, string>
                {
                    {
                        Core.Common.Constants.OpenApi.Name.Filter,
                        $"search.in({nameof(ProductReturnsSearchView.InventoryTrackerId).ToCamelCase()}, '{string.Join(",", dataChange.InventoryTrackerIds ?? [])}', ',') and {nameof(ProductReturnsSearchView.IsUnblindedData).ToCamelCase()} eq true"
                    }
                };

                var siteKitsGeneric = await _httpSearch.GetAllPagesGenericAsync(invQueryParams,
                    nameof(ProductReturnsSearchView), appContext!.StudySession!.Environment!, appContext!.StudySession!.SponsorId!,
                    cancellationToken);
                var siteKits = siteKitsGeneric.DeserializeAzureSearchResults<ProductReturnsSearchView>();
                result.AddRange((dataChange.InventoryTrackerIds ?? [])
                    .Join(siteKits,
                        invId => invId,
                        sk => sk.InventoryTrackerId,
                        (invId, sk) => new DataChangeInventory() {
                            InventoryTrackerId = sk?.Id!,
                            KitNumber = sk?.KitNumber!
                        }));
            }
            else
            {
                var invQueryParams = new Dictionary<string, string>
                {
                    {
                        Core.Common.Constants.OpenApi.Name.Filter,
                        $"search.in({nameof(SingleKitSearchView.InventoryTrackerId).ToCamelCase()}, '{string.Join(",", dataChange.InventoryTrackerIds ?? [])}', ',') and {nameof(SingleKitSearchView.IsUnblindedData).ToCamelCase()} eq true"
                    }
                };

                var depotKitsGeneric = await _httpSearch.GetAllPagesGenericAsync(invQueryParams,
                    nameof(SingleKitSearchView), appContext!.StudySession!.Environment!, appContext!.StudySession!.SponsorId!,
                    cancellationToken);
                var depotKits = depotKitsGeneric.DeserializeAzureSearchResults<SingleKitSearchView>();
                result.AddRange((dataChange.InventoryTrackerIds ?? [])
                    .Join(depotKits,
                        invId => invId,
                        sk => sk.InventoryTrackerId,
                        (invId, sk) => new DataChangeInventory() {
                            InventoryTrackerId = sk?.Id!,
                            KitNumber = sk?.KitNumber!
                        }));
            }
        }
        return result;
    }

    #region Private Methods

    private static string ValidateNumberRange(DataDictionaryResponse dataDictionaryConfig, decimal value)
    {
        if (dataDictionaryConfig.MinValue.HasValue && dataDictionaryConfig.MaxValue.HasValue && (value < dataDictionaryConfig.MinValue || value > dataDictionaryConfig.MaxValue))
        {
            return $"The entered value must be between {dataDictionaryConfig.MinValue} and {dataDictionaryConfig.MaxValue}.";
        }
        else if (dataDictionaryConfig.MinValue.HasValue && !dataDictionaryConfig.MaxValue.HasValue && value < dataDictionaryConfig.MinValue)
        {
            return $"The entered value must be greater than or equal to {dataDictionaryConfig.MinValue}.";
        }
        else if (!dataDictionaryConfig.MinValue.HasValue && dataDictionaryConfig.MaxValue.HasValue && value > dataDictionaryConfig.MaxValue)
        {
            return $"The entered value must be less than or equal to {dataDictionaryConfig.MaxValue}.";
        }
        return string.Empty;
    }

    private static bool SetCreatedActionStatus(KeyValuePair<string, bool> action, DataChangeResponseBase dataChange, DataChangeGroupResponse? dataChangeGroup, UserRolePermissionModel? userPermission, ApplicationContext appContext)
    {
        var canPerformAction = false;
        var isSuperAdmin = appContext.CurrentUser.IsSuperAdmin(appContext.StudySession!.Environment, appContext.CurrentUser.CurrentPreviewSession);
        if (action.Key == DataChangeAction.Delete.ToString().ToCamelCase())
        {
            var beenSubmitted = dataChange.History?.Exists(x => x.Action == DataChangeAction.Submit) ?? false;
            var permissionIds = new List<string>{ AppConstant.DataChangePermissions.Delete };
            var hasRolePermission = userPermission!.DoesUserHavePermission(permissionIds, null!);
            canPerformAction = (isSuperAdmin || hasRolePermission) &&
                !beenSubmitted &&
                (dataChange.Status.Key == DataChangeStatus.Created.ToString());
        }
        else if (action.Key == DataChangeAction.Edit.ToString().ToCamelCase())
        {
            var permissionIds = new List<string>{ AppConstant.DataChangePermissions.Edit };
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsRequestor.Contains(userPermission?.RoleId!) ?? false;
            var hasRolePermission = userPermission!.DoesUserHavePermission(permissionIds, null!);
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (isSuperAdmin || hasRolePermission) &&
                dataChange.Status.Key == DataChangeStatus.Created.ToString();
        }
        else if (action.Key == DataChangeAction.Submit.ToString().ToCamelCase())
        {
            var permissionIds = new List<string>{ AppConstant.DataChangePermissions.Add };
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsRequestor.Contains(userPermission?.RoleId!) ?? false;
            var hasRolePermission = userPermission!.DoesUserHavePermission(permissionIds, null!);
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (isSuperAdmin || hasRolePermission) &&
                dataChange.Status.Key == DataChangeStatus.Created.ToString();
        }
        return canPerformAction;
    }

    private static bool SetUnderReviewActionStatus(KeyValuePair<string, bool> action, DataChangeResponseBase dataChange, DataChangeGroupResponse? dataChangeGroup, UserRolePermissionModel? userPermission, ApplicationContext appContext)
    {
        var canPerformAction = false;
        var isSuperAdmin = appContext.CurrentUser.IsSuperAdmin(appContext.StudySession!.Environment, appContext.CurrentUser.CurrentPreviewSession);
        if (action.Key == DataChangeAction.Approve.ToString().ToCamelCase())
        {
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsApprover.Contains(userPermission?.RoleId!) ?? false;
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (dataChange.Status.Key == DataChangeStatus.Submitted.ToString());
        }
        else if (action.Key == DataChangeAction.Decline.ToString().ToCamelCase())
        {
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsApprover.Contains(userPermission?.RoleId!) ?? false;
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (dataChange.Status.Key == DataChangeStatus.Submitted.ToString());
        }
        else if (action.Key == DataChangeAction.Edit.ToString().ToCamelCase())
        {
            var permissionIds = new List<string>{ AppConstant.DataChangePermissions.Edit };
            var hasDataChangeGroupPermission = (dataChangeGroup?.RoleIdsRequestor.Contains(userPermission?.RoleId!) ?? false) ||
                (dataChangeGroup?.RoleIdsApprover.Contains(userPermission?.RoleId!) ?? false);
            var hasRolePermission = userPermission!.DoesUserHavePermission(permissionIds, null!);
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (isSuperAdmin || hasRolePermission) &&
                (dataChange.Status.Key == DataChangeStatus.Submitted.ToString());
        }
        else if (action.Key == DataChangeAction.SendBack.ToString().ToCamelCase())
        {
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsApprover.Contains(userPermission?.RoleId!) ?? false;
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (dataChange.Status.Key == DataChangeStatus.Submitted.ToString());
        }
        return canPerformAction;
    }

    private static bool SetSeconaryReviewActionStatus(KeyValuePair<string, bool> action, DataChangeResponseBase dataChange, DataChangeGroupResponse? dataChangeGroup, UserRolePermissionModel? userPermission, ApplicationContext appContext)
    {
        var canPerformAction = false;
        var isSuperAdmin = appContext.CurrentUser.IsSuperAdmin(appContext.StudySession!.Environment, appContext.CurrentUser.CurrentPreviewSession);
        if (action.Key == DataChangeAction.Approve.ToString().ToCamelCase())
        {
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsSecondaryApprover.Contains(userPermission?.RoleId!) ?? false;
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (dataChange.Status.Key == DataChangeStatus.Approved.ToString());
        }
        else if (action.Key == DataChangeAction.Decline.ToString().ToCamelCase())
        {
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsSecondaryApprover.Contains(userPermission?.RoleId!) ?? false;
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (dataChange.Status.Key == DataChangeStatus.Approved.ToString());
        }
        else if (action.Key == DataChangeAction.Edit.ToString().ToCamelCase())
        {
            var permissionIds = new List<string>{ AppConstant.DataChangePermissions.Edit };
            var hasDataChangeGroupPermission = (dataChangeGroup?.RoleIdsApprover.Contains(userPermission?.RoleId!) ?? false) ||
                (dataChangeGroup?.RoleIdsSecondaryApprover.Contains(userPermission?.RoleId!) ?? false);
            var hasRolePermission = userPermission!.DoesUserHavePermission(permissionIds, null!);
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (isSuperAdmin || hasRolePermission) &&
                (dataChange.Status.Key == DataChangeStatus.Approved.ToString());
        }
        else if (action.Key == DataChangeAction.SendBack.ToString().ToCamelCase())
        {
            var hasDataChangeGroupPermission = dataChangeGroup?.RoleIdsSecondaryApprover.Contains(userPermission?.RoleId!) ?? false;
            canPerformAction = (isSuperAdmin || hasDataChangeGroupPermission) &&
                (dataChange.Status.Key == DataChangeStatus.Approved.ToString());
        }
        return canPerformAction;
    }

    private Task<UserRolePermissionModel?> GetUserPermissionAsync(ApplicationContext context)
    {
        var isSuperAdminUser = context.CurrentUser.IsSuperAdmin(
               context.StudySession!.Environment!,
               context.CurrentUser.CurrentPreviewSession!);
        // lookup user permissions
        return _userRolePermissionService.GetUserPermissionAsync(
            isSuperAdminUser,
            context.CurrentUser.UserId ?? string.Empty,
            context.StudySession?.StudyId ?? string.Empty,
            context.StudySession?.SponsorId ?? string.Empty,
            context.SessionId ?? string.Empty,
            PermissionSection.System,
            context.CurrentUser,
            studyVersionId: context.StudySession?.StudyVersion ?? string.Empty,
            envrionmentId: context.StudySession?.Environment ?? string.Empty,
            previewSession: context.CurrentUser.CurrentPreviewSession!);
    }

    private string UpdateForTermMapping(string value)
    {
        return value switch
            {
                { } s when s == "Shipment" => "{{shipment| capitalize}}",
                { } s when s == "Patient" => "{{patient| capitalize}}",
                _ => value
            };
    }

    #endregion
}

//testing purposes//