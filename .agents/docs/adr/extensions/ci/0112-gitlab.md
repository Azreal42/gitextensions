# ADR 0112 · GitLab dans le suivi CI partagé

Reprendre les trois champs Windows Instance URL, Project ID et Api Token, leurs liens de lookup/gestion et les statuts de pipelines dans Browse. Le fournisseur garde `Gitlab` et `BuildServer.Gitlab.InstanceUrl/ProjectId/ApiToken/PagesLimit`, avec `BuildServer.EnableIntegration/ShowBuildResultPage/Type`.

La page utilise les brouillons XML communs : Effective en lecture seule sans lookup automatique ; Local/distribuée/Global éditables, Apply/Cancel et CAS optimiste partagés. Le lookup automatique attend l’activation visible d’une portée éditable. Les remotes doivent correspondre à l’instance ; les namespaces imbriqués sont admis et seul le suffixe `.git` est retiré. Les réponses périmées sont écartées, la fermeture annule puis draine les lectures.

HTTP/HTTPS explicites et PRIVATE-TOKEN conservent le contrat original. Le token masqué reste dans le XML choisi, sans coffre ni SSO ; sa portée doit l’ancrer à l’origine/basepath. PagesLimit=0 reste la sauvegarde Windows ; les bornes de sécurité signalent Truncated.

`GitLabApiClient` et `GitLabBuildSource` raccordent les pipelines au polling partagé et aux liens externes explicites. ShowInBuildReportTab=false vient du modèle Windows. Jobs/logs, écritures de pipelines et PR GitLab restent ouverts. [Preuves et limites](../../../todo.md#gitlab--périmètre-vérifié) ; page N2, aucune parité globale.
