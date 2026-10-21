# ADR 0014 · Dialogues Proxy et Gource

Statut : accepté.

Besoin : retrouver les choix et dispositions des plugins originaux ; utiliser les exécutables Linux configurés sans imposer de téléchargement Windows.

Décision : dialogues natifs dédiés. Proxy montre les portées Git locale/globale et garde le mot de passe hors préférences XDG. Gource choisit un exécutable et conserve les avatars temporaires jusqu'à la fin du viewer.

Settings → Plugins → Gource expose les deux champs Windows, chemin et arguments, via les intentions XML Local/Distributed/Global communes. Le dialogue Start lit ces mêmes valeurs effectives ; après un lancement accepté, il enregistre uniquement le chemin et les arguments modifiés. Le répertoire de travail reste propre à l’invocation. Annuler ou échouer avant le lancement n’enregistre pas la saisie.

Si l’écriture échoue après le lancement, la fenêtre garde la commande acceptée et l’erreur visibles. Start reprend uniquement la sauvegarde, sans lancer un second viewer ni rejouer un fichier déjà acquitté. Les modifications concurrentes restent protégées par la comparaison au profil lu. Le comportement de Proxy est inchangé ; aucun téléchargement Windows n’est ajouté.

Repères : `ProxySwitcherDialog`, `GourceDialog.StartAsync` / `SaveAcceptedAsync`, `MainWindow.Plugins`, `SimplePluginSettingsSession.StageAcceptedGource`, `PluginScopedSettingsSession.SaveFilesAsync`. Le rendu OpenGL de Gource réel reste à valider.
