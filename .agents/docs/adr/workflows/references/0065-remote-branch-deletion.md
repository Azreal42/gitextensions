# 0065 · Suppression de branches distantes

Statut : accepté sur un périmètre ciblé. Parité exhaustive ouverte.

Le dialogue doit permettre de choisir plusieurs branches/remotes comme Windows, sans confondre suppression distante et suppression des branches locales qui les suivent.

`DeleteRemoteBranchDialog` reprend la combo éditable, le sélecteur à cases partagé, les deux autorisations décochées et les huit candidats locaux maximum. Une branche non fusionnée demande Yes/No, avec No par défaut. Les menus de révision et Branches & tags ouvrent cette même fenêtre.

Le noyau valide toute la sélection avant le premier push. Il déduit la branche serveur des refspecs et les branches locales de la configuration réelle. Chaque remote est une transaction atomique, protégée par le hash confirmé ; une destination ou une référence changée est refusée. Les résultats restent distincts entre remotes. Annulation/transport après réception serveur peuvent laisser un résultat inconnu ; ne pas inventer de rollback.

Les destinations confirmées sont les URLs effectives calculées par Git (remote get-url --push --all), y compris insteadOf/pushInsteadOf. Sans pushurl explicite, toutes les URLs du remote sont des destinations push. Une sélection à plusieurs destinations, ou une réécriture changée depuis la confirmation, est refusée avant le premier push ; aucun nettoyage local ne suit ce refus.

Un Process propriétaire couvre toute la sélection pour conserver cette validation globale et les sorties Git réelles. Les branches locales ne sont proposées dans `DeleteBranchDialog` qu’après les succès distants prouvés et relecture de la configuration locale courante, après OK du Process ; leurs gardes et leur confirmation restent actives. Fermer un propriétaire arrête et draine les acteurs avant de reprendre sa fermeture, sans ouvrir le nettoyage local. Après Abort sans fermeture du propriétaire, la sélection de reprise retire les succès connus.

Écarts explicites : Windows ouvre un Process par remote ; événements de scripts Before/AfterPush non exécutés ; remotes à plusieurs destinations push effectives et serveurs sans atomic refusés. Le sélecteur partagé n’a pas encore le mode multicolonne >350 branches ni tous ses consommateurs Windows. Audit natif étendu restant ; tests dédiés dans `RemoteBranchDeletionScenarios` et `RemoteBranchDeletionDialogTests`.
