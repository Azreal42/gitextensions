# 0048 — Récupération depuis le Reflog

Statut : validé localement ; audit natif et paquet suivants requis.

## Intention

Retrouver un commit disparu du graphe courant et le rendre à nouveau accessible depuis la fenêtre historique Windows `FormReflog`.

## Décision

Afficher toutes les entrées de HEAD ou de la référence locale/distante choisie. Copier le SHA, créer une branche et ouvrir le Reset partagent les workflows existants et les textes/icônes XLF originaux.

La création démarre sans checkout, avec révision verrouillée et sans orphan. Le SHA est figé avant tout dialogue : une nouvelle entrée Reflog ne doit pas changer la cible. Reset garde Soft avec changements locaux, Hard sinon ; confirmation dirty avec No par défaut, refus en HEAD détaché et relecture de la branche après confirmation.

Repères : `ReflogDialog`, `GitRepository.Advanced/ReflogEncoding`, `CreateBranchDialog`, `ResetCurrentBranchDialog`, routes `MainWindow.Browser/Workflows`. Les notes personnalisées aux codecs ambigus restent dans [Failure](../../../failure.md).
