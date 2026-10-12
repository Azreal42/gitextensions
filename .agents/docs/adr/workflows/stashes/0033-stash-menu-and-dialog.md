# ADR 0033 · Menu et fenêtre Stash

Statut : accepté pour le menu et les fenêtres de base ; fonctions contextuelles avancées encore ouvertes.

FormBrowse/FormStash et la capture Windows définissent le bouton scindé. Le segment principal ouvre Manage ; dropdown ancré à gauche : Stash, Stash pop, séparateur, Manage stashes, Create a stash. Stash staged apparaît avec Git >= 2.35. Débordement et triangle partagés avec Browse ; contrôles désactivés sur bare.

Ctrl+Alt+Up enregistre immédiatement sans message ni Keep index ; Include untracked vient du réglage durable, faux par défaut. Ctrl+Alt+Down applique/supprime le dernier stash, indépendamment du graphe/sélection. Ctrl+Shift+Alt+Up enregistre l'index seul. Les touches fonctionnent aussi depuis le popup.

Une fenêtre reprend Create (changements courants) et Manage (premier stash existant) : Show, FileStatusList partagé Index/Workspace, message sur fond Info, Keep index/untracked, all/selected, Drop/Apply ; lecteur diff/image à droite avec codecs, options et recherche partagés. Splitter réglable, XLF originaux et préférences XDG ; légendes françaises longues visibles.

Chemins sélectionnés avec `:(literal)`, sans `--literal-pathspecs` global qui casse le nettoyage non suivi. Renommage partiel : conserver le contrat de `git stash push -- <nouveau nom>` ; la suppression indexée de l'ancien nom peut rester, sans reset implicite. Aperçu : premier parent pour le diff, troisième pour les non suivis ; chemins NUL/octets binaires conservés. Message complet `%B`, car `%s` aplatit les retours à la ligne.

Apply conserve le stash ; Apply/Pop sans `--index`, comme Windows. Drop confirme avec No par défaut et suppression volontaire durable de la confirmation, puis sélectionne le voisin. Refuser une identité obsolète avant mutation pour éviter le nouveau stash@{n}. Sérialiser les mutations, garder erreurs/annulations visibles et rafraîchir Browse après tentative. Pop en conflit conserve le stash et ouvre résolution/staging via PatchConflictDialog, sans inventer un merge à continuer/abandonner.

F5 rafraîchit, Ctrl+N/P parcourt les stashes. Échap ferme le popup, désélectionne le texte, puis ferme ; fermeture busy annule/attend Git et descendants. Les anciennes APIs StashAsync/ApplyStashAsync restent compatibles ; Browse utilise les options/identités typées.

Repères : `MainWindow.Stashes`, `StashDialog`, `StashPreferencesStore`, `GitRepository.Stashes`, `StashMenuTests`, `StashScenarios`. Preuves de ce lot : [snapshot 36aae198f](../../../../../artifacts/linux/snapshots/36aae198f/proofs/root-acceptance.json), tests locaux `artifacts/linux/stash-development-tests`. Le parcours accepté sous Xvfb sans WM ferme par Échap après focus explicite ; pas de preuve de premier clic/Wayland. Périmètre restant et dernier paquet : [TODO](../../../todo.md).
