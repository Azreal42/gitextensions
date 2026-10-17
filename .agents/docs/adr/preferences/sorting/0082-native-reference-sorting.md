# ADR 0082 — Tri natif des références et priorités

## Intention

Retrouver branches et remotes dans l’ordre choisi sous Windows, y compris dans les listes bornées et le panneau gauche, en gardant la sélection et le premier geste clavier.

## Décision

- Partager les neuf critères et les deux sens natifs entre lecteurs et menus. Trier avant les limites de 100/1 000 références ; appliquer les priorités aux branches complètes avant leurs dossiers, séparément pour les groupes de remotes. Garder l’ordre stable à priorité égale.
- Conserver les motifs Windows : niveaux séparés par `;`, alternatives par `|`, expressions ancrées et sensibles à la casse, `ExplicitCapture` et première correspondance.
- GitDefault n’ajoute aucune option globale de tri de l’historique ; une option explicite du filtre reste prioritaire. Préserver les valeurs JSON Topology=0, AuthorDate=1 et Date=2 ; GitDefault=3 devient le défaut absent/invalide.
- Distinguer ordre brut du lecteur et ordre visible du graphe : le graphe Windows peut repousser un parent après un enfant lu tardivement. Garder cet algorithme et vérifier ces deux contrats séparément.
- View change la session ; Save as default sauvegarde la vue. OK Settings enregistre les références et le seul champ Sort de l’historique, sans sauvegarder les autres choix View de session. Cancel ne les écrit pas.
- Garder les aides sur les trois icônes, comme Windows. Une aide ajoutée à l’éditeur recouvrait le champ voisin et empêchait son premier clic ; sa suppression garde l’aide accessible sans geste réparateur.

## Adaptations et limites

Valider tous les motifs avant OK et borner le matching ; garder la saisie après erreur. Un nom de remote contenant `/` est séparé avec son vrai nom connu : cette amélioration diffère du découpage Windows au premier `/`.

CommitInfo porte un périmètre partagé avec son comparateur et son filtre Windows distincts (ADR [0083](../../ui/history/0083-shared-commit-information.md)), dont les compléments et variantes restent ouverts. Remotes vides/inactifs et paramètres globaux/locaux/distribués restent à porter. Badges et QuickRef conservent leurs comparateurs distincts. Le niveau N2 ne certifie ni ces fonctions, ni toutes les variantes natives, Wayland, DPI/accessibilité ou traductions.

## Repères et validation

`GitReferenceSorting`, `GitReferencePriorities`, `MainWindow.ReferenceSorting`, `DesktopSettingsDialog` et `HistoryViewPreferencesStore`. Le [TODO](../../../todo.md) relie les suites, le paquet et les parcours ciblés sur `5a52db1a9`. Le banc long `0ac349f18`, interrompu avec un retour externe 143, reste une preuve partielle non acceptée. Les audits antérieurs gardent leur source.
