# ADR 0032 · Menu Pull/Fetch original

Statut : accepté pour le menu ; dialogue avancé et migration globale encore ouverts.

La capture Windows et FormBrowse/FormPull définissent les six actions et leurs icônes. Reprendre le bouton scindé compact, dropdown ancré sur le bord gauche du segment principal, aussi accessible dans le débordement. Le sous-menu Set default Pull button action reste ouvert, coche exclusivement le choix et n'exécute aucun Git. Légendes/mnémoniques XLF et `GitPullAction` original sont partagés.

DefaultPullAction et FormPullAction restent distincts, Merge par défaut. XDG conserve le défaut du bouton, le dernier mode du dialogue et la suppression volontaire de confirmation FetchPruneAll. F8/bouton exécutent le défaut, Ctrl+Down ouvre le dialogue ; Ctrl+Shift+Down fait QuickFetch sans remote/refspec, Ctrl+Shift+P lance Merge. Ces touches fonctionnent depuis les popups. FetchAll disparaît à zéro/un remote sans remplacer un défaut enregistré.

Le menu rapide utilise le remote de la branche checkoutée, ou le premier réel si absent/invalide ; le commit sélectionné ne le change pas. Champ de branche distant initialement vide. Fetch/FetchAll gardent fetch.prune/tagopt configurés ; PruneAll ajoute `--all --prune --force`, sans `--prune-tags`, après confirmation. Les anciens appelants FetchAsync/PullAsync restent compatibles.

Merge impose `--no-rebase` pour respecter le mode visible et réapplique `--ff-only` si configuré : sur Git récent, `--no-rebase` prime aussi sur pull.ff. Rebase passe `--rebase` et conserve la priorité native mesurée par Git. Aucune configuration Git réécrite.

Le dialogue garde remote/branche littéraux, Merge/Rebase/Fetch et pruning, deux flux d'erreur, résolution/continue/abort. Token Main dès l'initialisation ; erreur/annulation rafraîchit Browse sans ouvrir de repli après annulation rapide. [ All ] force Fetch. Confirmation rebase d'un merge : No par défaut sans commande Merge. Bare autorise Fetch seulement, sans convertir les autres raccourcis en fetchs. HEAD détaché propose checkout/continuer/annuler ; autre remote exige branche ou confirmation ; wildcards refusés pour Pull.

Repères : `MainWindow.Pull`, `MainWindow.Toolbar/Shortcuts`, `PullDialog`, `PullPreferencesStore`, `GitRepository.Network`, `PullMenuTests`, `PullFetchScenarios`. Original : `FormBrowse.InitMenusAndToolbars`, `FormPull`, `HotkeySettingsManager`, `GitModule.FetchCmd/PullCmd`.

Pièges résolus : placement du présentateur et radios Fluent reprenant l'accent OS (voir [Failure](../../../failure.md)). Les quatre flèches de Browse partagent le même triangle 6×3, couleur du contrôle et ligne de peinture ; menus/zones de clic conservés.

Preuves natives du menu : [snapshot 94f44d78f](../../../../../artifacts/linux/snapshots/94f44d78f/proofs/root-acceptance.json) ; alignement des flèches : [snapshot a4cf38dbf](../../../../../artifacts/linux/snapshots/a4cf38dbf/proofs/root-acceptance.json). Les candidats refusés gardent leurs propres captures. Le focus explicite des copies sous Xvfb sans WM ne prouve pas un premier clic natif/Wayland. Options tags/unshallow/autostash/sous-modules et sortie continue : [TODO](../../../todo.md).
