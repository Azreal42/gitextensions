# ADR 0114 — Azure DevOps et TFS

Conserver la page Windows à trois champs et deux liens : URL de projet, filtre de définition, PAT masqué. Réutiliser les portées XML, brouillons et publication explicite du suivi CI commun.

Le PAT n'est autorisé que pour les URLs concrétisées par les vraies variables du dépôt dans la portée choisie. Une validation syntaxique de template ne crée pas une autorisation. Extraction de lien et messages restent possédés et annulables avec Settings.

`AzureDevOpsApiClient` lit les routes historiques, `AzureDevOpsSnapshot` projette les états par SHA, `AzureDevOpsBuildSource` rejoint le polling commun. L'exclusion Windows du rapport embarqué est conservée. GCM, identité OS/SSO et serveurs Azure/TFS réels restent des périmètres distincts. Repères : `AzureDevOpsSettingsPage`, `MainWindow.AzureDevOps`.
