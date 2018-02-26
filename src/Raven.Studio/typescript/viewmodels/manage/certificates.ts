import viewModelBase = require("viewmodels/viewModelBase");
import database = require("models/resources/database");
import databasesManager = require("common/shell/databasesManager");
import certificateModel = require("models/auth/certificateModel");
import getCertificatesCommand = require("commands/auth/getCertificatesCommand");
import certificatePermissionModel = require("models/auth/certificatePermissionModel");
import uploadCertificateCommand = require("commands/auth/uploadCertificateCommand");
import deleteCertificateCommand = require("commands/auth/deleteCertificateCommand");
import replaceClusterCertificateCommand = require("commands/auth/replaceClusterCertificateCommand");
import updateCertificatePermissionsCommand = require("commands/auth/updateCertificatePermissionsCommand");
import getNextOperationId = require("commands/database/studio/getNextOperationId");
import notificationCenter = require("common/notifications/notificationCenter");
import endpoints = require("endpoints");
import copyToClipboard = require("common/copyToClipboard");
import popoverUtils = require("common/popoverUtils");
import messagePublisher = require("common/messagePublisher");
import eventsCollector = require("common/eventsCollector");

interface unifiedCertificateDefinitionWithCache extends unifiedCertificateDefinition {
    expirationClass: string;
    expirationText: string;
    expirationIcon: string;
}

class certificates extends viewModelBase {

    spinners = {
        processing: ko.observable<boolean>(false)
    };
    
    model = ko.observable<certificateModel>();
    showDatabasesSelector: KnockoutComputed<boolean>;
    hasAllDatabasesAccess: KnockoutComputed<boolean>;
    canExportClusterCertificates: KnockoutComputed<boolean>;
    canReplaceClusterCertificate: KnockoutComputed<boolean>;
    certificates = ko.observableArray<unifiedCertificateDefinition>();
    
    usingHttps = location.protocol === "https:";
    
    resolveDatabasesAccess = certificateModel.resolveDatabasesAccess;

    importedFileName = ko.observable<string>();
    
    newPermissionDatabaseName = ko.observable<string>();
    
    newPermissionValidationGroup: KnockoutValidationGroup = ko.validatedObservable({
        newPermissionDatabaseName: this.newPermissionDatabaseName
    });

    generateCertificateUrl = endpoints.global.adminCertificates.adminCertificates;
    exportCertificateUrl = endpoints.global.adminCertificates.adminCertificatesExport;
    generateCertPayload = ko.observable<string>();

    clearanceLabelFor = certificateModel.clearanceLabelFor;
    
    constructor() {
        super();

        this.bindToCurrentInstance("onCloseEdit", "save", "enterEditCertificateMode", 
            "deletePermission", "addNewPermission", "fileSelected", "copyThumbprint",
            "useDatabase", "deleteCertificate");
        this.initObservables();
        this.initValidation();
    }
    
    activate() {
        return this.loadCertificates();
    }
    
    compositionComplete() {
        super.compositionComplete();

        this.model.subscribe(model  => {
            if (model) {
                this.initPopover();
            }
        });
    }
    
    private initPopover() {
        popoverUtils.longWithHover($(".certificate-file-label small"),
            {
                content: 'Select .pfx store file with single or multiple certificates. All of them will be imported under a single name.',
                placement: "top"
            });
    }
    
    private initObservables() {
        this.showDatabasesSelector = ko.pureComputed(() => {
            if (!this.model()) {
                return false;
            }
            
            return this.model().securityClearance() === "ValidUser";
        });
        
        this.canExportClusterCertificates = ko.pureComputed(() => {
            const certs = this.certificates();
            return _.some(certs, x => x.SecurityClearance === "ClusterNode");
        });
        
        this.canReplaceClusterCertificate = ko.pureComputed(() => {
            const certs = this.certificates();
            return _.some(certs, x => x.SecurityClearance === "ClusterNode");
        });
    }
    
    private initValidation() {
        this.newPermissionDatabaseName.extend({
            required: true
        });
    }
    
    enterEditCertificateMode(itemToEdit: unifiedCertificateDefinition) {
        this.model(certificateModel.fromDto(itemToEdit));
        this.model().validationGroup.errors.showAllMessages(false);
    }

    deleteCertificate(certificate: Raven.Client.ServerWide.Operations.Certificates.CertificateDefinition) {
        this.confirmationMessage("Are you sure?", "Do you want to delete certificate with thumbprint: " + certificate.Thumbprint + "", ["No", "Yes, delete"])
            .done(result => {
                if (result.can) {
                    eventsCollector.default.reportEvent("certificates", "delete");
                    new deleteCertificateCommand(certificate.Thumbprint)
                        .execute()
                        .always(() => this.loadCertificates());
                }
            });
    }

    exportClusterCertificates() {
        eventsCollector.default.reportEvent("certificates", "export-certs");
        const targetFrame = $("form#certificates_export_form");
        targetFrame.attr("action", this.exportCertificateUrl);
        targetFrame.submit();
    }
    
    enterGenerateCertificateMode() {
        eventsCollector.default.reportEvent("certificates", "generate");
        this.model(certificateModel.generate());
    }
    
    enterUploadCertificateMode() {
        eventsCollector.default.reportEvent("certificates", "upload");
        this.model(certificateModel.upload());
    }
    
    replaceClusterCertificate() {
        eventsCollector.default.reportEvent("certificates", "replace");
        this.model(certificateModel.replace());
    }

    fileSelected(fileInput: HTMLInputElement) {
        if (fileInput.files.length === 0) {
            return;
        }
        
        const fileName = fileInput.value;
        const isFileSelected = fileName ? !!fileName.trim() : false;
        this.importedFileName(isFileSelected ? fileName.split(/(\\|\/)/g).pop() : null);
        
        const file = fileInput.files[0];
        const reader = new FileReader();
        reader.onload = () => {
            const dataUrl = reader.result;
            // dataUrl has following format: data:;base64,PD94bW... trim on first comma
            this.model().certificateAsBase64(dataUrl.substr(dataUrl.indexOf(",") + 1));
        };
        reader.onerror = function(error: any) {
            alert(error);
        };
        reader.readAsDataURL(file);
    }

    save() {
        this.newPermissionValidationGroup.errors.showAllMessages(false);
        
        if (!this.isValid(this.model().validationGroup)) {
            return;
        }
        
        this.spinners.processing(true);
        
        const model = this.model();
        switch (model.mode()) {
            case "generate":
                this.generateCertPayload(JSON.stringify(model.toGenerateCertificateDto()));
                
                new getNextOperationId(null)
                    .execute()
                    .done(operationId => {
                        
                        const targetFrame = $("form#certificate_download_form");
                        targetFrame.attr("action", this.generateCertificateUrl + "?operationId=" + operationId);
                        targetFrame.submit();

                        notificationCenter.instance.monitorOperation(null, operationId)
                            .done(() => {
                                messagePublisher.reportSuccess("Client certificate was generated.");
                            })
                            .fail(() => {
                                notificationCenter.instance.openDetailsForOperationById(null,  operationId);
                            })
                            .always(() => {
                                this.spinners.processing(false);
                                this.onCloseEdit();
                                this.loadCertificates();
                            })
                    })
                    .fail(() => {
                        this.spinners.processing(false);
                        this.onCloseEdit();
                    });
                break;
            case "upload":
                new uploadCertificateCommand(model)
                    .execute()
                    .always(() => {
                        this.spinners.processing(false);
                        this.loadCertificates();
                        this.onCloseEdit();
                    });
                break;
                
            case "editExisting":
                new updateCertificatePermissionsCommand(model)
                    .execute()
                    .always(() => {
                        this.spinners.processing(false);
                        this.loadCertificates();
                        this.onCloseEdit();
                    });
                break;
                
            case "replace":
                new replaceClusterCertificateCommand(model)
                    .execute()
                    .always(() => {
                        this.spinners.processing(false);
                        this.loadCertificates();
                        this.onCloseEdit();
                    });
        }
    }
    
    private loadCertificates() {
        return new getCertificatesCommand(true)
            .execute()
            .done(certificates => {
                const mergedCertificates = [] as Array<unifiedCertificateDefinition>;
                
                const secondaryCertificates = [] as Array<Raven.Client.ServerWide.Operations.Certificates.CertificateDefinition>;
                
                certificates.forEach(cert => {
                    if (cert.CollectionPrimaryKey) {
                        secondaryCertificates.push(cert);
                    } else {
                        (cert as unifiedCertificateDefinition).Thumbprints = [cert.Thumbprint];
                        mergedCertificates.push(cert as unifiedCertificateDefinition);
                    }
                });
                
                secondaryCertificates.forEach(cert => {
                    const thumbprint = cert.CollectionPrimaryKey.split("/")[1];
                    const primaryCert = mergedCertificates.find(x => x.Thumbprint === thumbprint);
                    primaryCert.Thumbprints.push(cert.Thumbprint);
                });
                
                this.updateCache(mergedCertificates);
                this.certificates(mergedCertificates); 
            });
    }
    
    private updateCache(certificates: Array<unifiedCertificateDefinition>) {
        certificates.forEach((cert: unifiedCertificateDefinitionWithCache) => {
            const date = moment.utc(cert.NotAfter);
            const dateFormatted = date.format("YYYY-MM-DD");
            
            const nowPlusMonth = moment.utc().add(1, 'months');
            
            if (date.isBefore()) {
                cert.expirationText = 'Expired ' + dateFormatted;
                cert.expirationIcon = "icon-danger";
                cert.expirationClass = "text-danger"
            } else if (date.isAfter(nowPlusMonth)) {
                cert.expirationText = "Expiring " +  dateFormatted;
                cert.expirationIcon =  "icon-clock";
                cert.expirationClass = "";
            } else {
                cert.expirationText = "Expiring " + dateFormatted;
                cert.expirationIcon =  "icon-warning";
                cert.expirationClass = "text-warning";
            }
        });
    }
    
    onCloseEdit() {
        this.model(null);
    }
    
    deletePermission(permission: certificatePermissionModel) {
        const model = this.model();
        model.permissions.remove(permission);
    }

    useDatabase(databaseName: string) {
        this.newPermissionDatabaseName(databaseName);
        this.addNewPermission();
    }
    
    addNewPermission() {
        if (!this.isValid(this.newPermissionValidationGroup)) {
            return;
        }
        
        const permission = new certificatePermissionModel();
        permission.databaseName(this.newPermissionDatabaseName());
        permission.accessLevel("ReadWrite");
        this.model().permissions.push(permission);
        this.newPermissionDatabaseName("");
        this.newPermissionValidationGroup.errors.showAllMessages(false);
    }

    createDatabaseNameAutocompleter() {
        return ko.pureComputed(() => {
            const key = this.newPermissionDatabaseName();
            
            const existingPermissions = this.model().permissions().map(x => x.databaseName());
            
            const dbNames = databasesManager.default.databases()
                .map(x => x.name)
                .filter(x => !_.includes(existingPermissions, x));

            if (key) {
                return dbNames.filter(x => x.toLowerCase().includes(key.toLowerCase()));
            } else {
                return dbNames;
            }
        });
    }
    
    copyThumbprint(thumbprint: string) {
        copyToClipboard.copy(thumbprint, "Thumbprint was copied to clipboard.");
    }
    
}

export = certificates;
