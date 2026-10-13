# 0049 — Récupération des objets perdus

Statut : code et candidat Debian `2d36958fd` vérifiés ; audit natif requis avant livraison.

## Intention

Retrouver les commits et fichiers devenus inaccessibles avec les six workflows de `FormVerify` : scan/filtres, aperçu/copie, branche/tag, export blob, tags de récupération et lost-found/prune.

## Décision

Partager les dialogues Branch/Tag ordinaires : SHA initial figé avant leur ouverture, choix explicites de révision, checkout et orphan conservés. Une sélection vide ne supprime aucun tag existant. Les options fsck suivent `FormVerify`, distinctes du contrat Maintenance existant ; Full décoché omet le flag.

Créer les tags sur les OID exacts, sans décapsuler les tags annotés. Compter uniquement les effets confirmés ; signaler les réussites partielles, divergences et effets inconnus, sans annoncer de rollback fictif.

L’export conserve les octets et la conversion CRLF historique ; les vrais chemins Git restent protégés après résolution des liens. Le prune suit la commande Git historique après confirmation.

Repères : `RecoverLostObjectsDialog`, `GitRepository.Objects*`, `CreateBranchDialog`, `CreateTagDialog`, `MainWindow.Browser/Exports`. Preuves et validations restantes dans [TODO](../../../todo.md).
