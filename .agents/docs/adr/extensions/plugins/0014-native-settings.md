# ADR 0014 · Dialogues Proxy et Gource

Statut : accepté.

Besoin : retrouver les choix et dispositions des plugins originaux ; utiliser les exécutables Linux configurés sans imposer de téléchargement Windows.

Proxy montre http.proxy effectif/global et conserve Set/Unset comme opérations Git explicites. Ses quatre défauts Windows utilisent la page Plugins et les intentions XML Local/Distributed/Global communes, avec Effective en lecture seule. Le mot de passe est masqué et enregistré dans la portée choisie uniquement par Apply/OK ; aucun writer JSON à la fermeture. L’ancien JSON fournit seulement host/port/username, sans importer un password ni des credentials intégrés au host, sans migration implicite.

Settings… ferme l’aperçu et ouvre la même page possédée depuis le menu ou un script. Cette action n’applique pas le proxy Git et ne recharge pas la grille Browse. Le prochain lancement lit uniquement les valeurs acquittées. Les quatre champs restent des strings comme Windows : la validation du proxy appartient au vrai Set, pas à Apply.

Gource conserve les avatars temporaires jusqu’à la fin du viewer. Settings → Plugins → Gource expose ses deux champs Windows, chemin et arguments, via les mêmes intentions XML. Après un lancement accepté, Start enregistre uniquement ces champs modifiés ; le répertoire de travail reste propre à l’invocation. Annuler ou échouer avant le lancement n’enregistre pas la saisie.

Si l’écriture échoue après le lancement, la fenêtre garde la commande acceptée et l’erreur visibles. Start reprend uniquement la sauvegarde, sans second viewer ni rejeu d’un fichier acquitté. Les modifications concurrentes restent protégées par comparaison au profil lu.

Repères : `ProxySwitcherDialog/SettingsSession`, `MainWindow.Plugins`, `PluginScopedSettingsSession/Page`, `GourceDialog.StartAsync/SaveAcceptedAsync`, `SimplePluginSettingsSession.StageAcceptedGource`. Le rendu OpenGL de Gource réel et les audits natifs étendus restent ouverts.
