# ADR 0109 · Lecture CI GitHub Actions

Décision : un fournisseur Actions en lecture seule, désactivé par défaut. Pages CI et GitHub Actions N2 ; [périmètre](../../../todo.md#contributeurs-console-liens-et-github-actions--validation-ciblée).

Les clés XML Windows `BuildServer.*` et `BuildServer.GitHub Actions.*` utilisent les scopes et la baseline acquittée communs ([ADR0105](../../preferences/detailed/0105-detailed-graph-and-xml-settings.md)). Apply publie les changements ; Cancel abandonne le reste. Détection owner/repository sans écriture ; token masqué, explicite et stocké dans le fichier choisi. Aucun coffre ou dialogue générique de credentials n’est déduit de ce champ.

`GitHubActionsApiClient` fait seulement GET : pages numériques de 100, borne explicite, ETag/304 et cache 30 s ; aucune publication partielle après erreur/annulation. Les workflows frères restent distincts par SHA ; états Windows et inconnus conservés. Le token exige HTTPS, reste lié à l’origine configurée et n’est transmis ni par Link ni vers une redirection d’autre origine ; erreurs affichées fixes sans payload/secret. `MainWindow.GitHubActions` possède polling/rafraîchissements et drainage à Close. Icône/texte de grille et liste locale des runs affichent les résultats ; seul un geste ouvre la page de run du site configuré dans le navigateur externe. Build report reste une liste, sans viewer HTML ni détails jobs/logs équivalents Windows.

Aucune création/liste de PR, mutation de workflow, jobs/logs détaillés ou intégration TeamCity/Jenkins/AppVeyor/Azure DevOps n’est portée par ce lecteur. Tests HTTP contrôlés, accès live, natif et paquet restent des périmètres distincts.
