# 0067 · Configuration des remotes

Statut : accepté sur un périmètre ciblé ; audit natif exhaustif ouvert.

Le développeur retrouve les deux onglets de FormRemotes : remotes actifs/inactifs et tracking des branches. Browse, Pull et Push ouvrent la même fenêtre. Sauvegarder un remote déclenche Fetch puis actualise les choix ; modifier le tracking valide à la perte de focus, sans déplacer HEAD ni lancer de transfert.

Reprendre les dispositions, icônes, traductions et normaliseur Windows. Les cadres et petits contrôles sont partagés avec Pull ; le réglage avancé rend Prefix et Color accessibles. Les couleurs par remote sont consommées par le graphe, sans changer les couleurs par défaut.

Conserver les listes d’URL ordonnées, clés inconnues, refspecs et réglages non exposés. L’éditeur modifie la première URL fetch et la dernière push explicite ; sans push séparé, Git utilise toutes les URL fetch. L’affichage Pull/Push respecte les resets de listes. PuTTY/Pageant n’est pas une deuxième gestion des identifiants Linux : conserver les anciennes clés opaques et utiliser Git/OpenSSH/askpass existants.

Les copies privées sont préparées par Git puis publiées sous config.lock, avec contrôles d’identité/contenu/includes et coordination par common-directory. Une écriture de tracking ne rebase les autres buffers que sur la révision de configuration retournée par cette écriture. Les étapes natives rename/remove peuvent avoir des effets partiels : signaler leur état réel, sans rollback inventé.

Désactiver reprend le comportement Windows : déplacer toutes les valeurs vers [-remote], supprimer les références distantes et les upstreams via Git ; réactiver ne restaure pas ces références/upstreams. Les branches et fichiers locaux restent présents. Les noms voisins (team/team.foo) restent distincts ; une collision active/inactive est refusée.

Ne pas modifier global/includes silencieusement : les opérations structurelles sur une section héritée et les effacements que Git ne sait pas masquer sont refusés avant mutation. Les resets d’URL héritées exigent le support natif de Git. Sur Git 2.53, un branch.remote hérité ne peut pas être masqué par une valeur vide sans casser for-each-ref ; proposer une valeur valide ou éditer la source.

Save recharge les listes même sans champ modifié, comme Initialize sous Windows. Sérialiser ce rechargement avec les validations de tracking ; réserver sa place avant toute désactivation ou perte de focus réentrante. Une valeur validée pendant une écriture doit atteindre Git et rester affichée sans clic supplémentaire.

Repères : RemotesDialog.*, GitRepository.RemoteSettings.*, GitRemoteSettings, ClassicChoiceAppearance, MainWindow.Workflows, PullDialog.Source, PushDialog. Référence Windows : FormRemotes et ConfigFileRemoteSettingsManager.

Validation ciblée : RemotesDialogTests, RemotesTrackingValidationTests, RemoteReferenceColorTests et RemoteSettingsScenarios ; exécutions complètes et paquet liés au commit dans le TODO. La grille de tracking utilise une liste sortable ; redimensionnement des colonnes et copie de cellules WinForms restent à reprendre. Scripts Before/AfterFetch et audit natif étendu restent ouverts. N3 ne vaut pas parité intégrale.
