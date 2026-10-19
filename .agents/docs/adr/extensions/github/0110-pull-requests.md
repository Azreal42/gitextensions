# ADR 0110 · Pull requests GitHub et identifiants partagés

Décision : porter Create/View dans le menu Git hosts avec un client REST dédié ; réutiliser FileStatusListView, DiffView, Checkout et les événements Scripts. Les deux fenêtres restent N2 ; le dialogue générique d’identifiants reste N1 tant que les fournisseurs CI historiques ne le consomment pas.

Le groupe GitHub `PluginSettings` original reste dans `preferences.json`, indépendant du token CI Actions. Save fusionne les seuls champs édités, préserve les inconnus et refuse les conflits ; remplacement privé en 0600 avec vérification optimiste des octets, sans transaction interprocessus promise. Les identifiants sont liés à l’origine API explicite ; HTTPS requis hors Guest. Le dialogue clone Guest/UsernameAndPassword/BearerToken ; OK publie un brouillon au parent, Cancel ne sauvegarde rien. Aucun coffre implicite.

Create conserve la saisie pendant les lectures du titre Git, du template et des branches, puis crée par POST explicite. View garde les brouillons par PR, partage les lecteurs de fichiers/diff et propose commentaire POST/fermeture PATCH explicites. Les éditeurs sont des champs multilignes ordinaires ; les règles du message de commit ne s’appliquent pas aux PR. Lectures bornées, générations annulables et fermeture drainée ; aucune répétition automatique des écritures.

Fetch cible `pr/n12_feature/topic` ou `owner/feature/topic`, sans force/prune/tags. Le second parcours crée ou réutilise un remote compatible puis passe par Checkout partagé ; un veto/échec conserve les effets Git déjà acquis, sans rollback global.

Limites : discussion Markdown/HTML littérale, variantes visuelles et audits étendus, autres hébergeurs/fournisseurs CI. Repères : `MainWindow.PullRequests`, `GitHubHostingClient`, `GitHubPullRequestDiff`, `GitRepository.PullRequests` et leurs tests. [Qualification et écarts](../../../todo.md#pull-requests-et-identifiants--qualification-partielle).
