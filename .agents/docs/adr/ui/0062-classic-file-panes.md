# ADR 0062 · Panneaux Diff / File tree et menu de fichiers

## Références et écarts

Comparaison avec les captures Windows fournies et les sources `FileStatusList.cs`, `.Designer.cs`, `.ContextMenu.cs`, `.TreeContextMenu.cs`, `.Toolbar.cs`, `CopyPathsToolStripMenuItem` et `RevisionDiffControl.cs`.

Le File tree Linux était une ListBox plate et réutilisait des barres de workflow génériques. Le groupe Diff unique était supprimé. Les lignes de fichiers restaient à 24 px ; le contenu d’un fichier conservé en sélection était effacé lors d’un changement de commit sans être rechargé.

## Structure partagée

Diff et File tree utilisent FileStatusListView / FileStatusListBuilder et le même TreeView natif que les listes Changes/Commit/Stash. Entêtes 18 px, icônes originales 16 px, retrait 14 px comme FileStatusList Windows (la navigation Branches conserve 19 px). Les lignes racines sont masquées en Diff et visibles en File tree. Le décor de pointillés reste non interactif ; les templates, raccourcis d’arbre et sélection restent natifs. Le mode File tree impose le regroupement par chemin et masque sa barre, indépendamment du tri partagé des listes de différences.

Le filtre regex éditable reste en haut avec son indication italique. Le Diff affiche le groupe de comparaison même s’il est seul, son compte et le sujet du commit de base disponible ; les chemins plats distinguent répertoire atténué et nom du fichier. Le préfixe d’une ligne sélectionnée claire reprend le gris 192 de DrawNodeImpl. Le séparateur est de 4 px. Le File tree ne contient plus les boutons génériques Blame/File history/Search ni un sélecteur d’encodage permanent au-dessus du contenu ; les commandes restent accessibles dans leurs menus.

Le contenu est lu depuis le commit capturé dans le File tree. Un changement de commit ou le rafraîchissement de ce panneau recharge le chemin sélectionné ; le contenu du worktree ne remplace jamais le blob historique. Grep conserve ses chemins littéraux, octets, encodages, colonnes et annulation.

## Menu et actions

Un constructeur commun produit les menus Diff/File tree avec OriginalMenuItems et ClassicMenuAppearance : ordre, images, gestes, séparateurs et sous-menus du Designer. Sélection par clic droit avant ouverture, sélection multiple/folders logique, reset vers A/B avec ResetChangesDialog déjà employé par Stash, difftool, ouverture worktree/révision temporaire, application explicite, sauvegarde, rename/move, copie POSIX/native, dossier, filtre historique, historique, blame, recherche de fichier et git-grep, arrêt du suivi. Les dossiers ont Select all/Collapse all/Expand all et Collapse root folders comme le contrôle Windows. Le double-clic ouvre File history conformément à RevisionDiffControl.

Les actions Git utilisent des argv et pathspecs littéraux. Le reset prévalide les collisions non suivies et les parents symboliques avant de modifier l’index ; la suppression des nouveaux fichiers exige la case du dialogue d’origine. Annuler ne modifie rien. Les contrôles de chemins sont partagés avec le reset Stash. L’ouverture temporaire utilise un dossier privé et conserve exactement les octets du blob ; nettoyage à la fermeture. Blame garde ligne affichée et encodage de session pour le même blob.

## Adaptations et limites

Linux emploie xdg-open pour les associations de fichiers/dossiers ; Open with demande un exécutable explicite. Les entrées Visual Studio, WSL et Cygwin ne sont pas affichées. Les icônes natives d’associations Windows sont remplacées par File.png (FolderSubmodule.png pour gitlinks). Le menu difftool contient les comparaisons A/B/worktree ; mémorisation de deux fichiers, outils personnalisés Windows et sous-menus scripts avancés restent ouverts. Le git-grep lancé depuis Diff ouvre la recherche du File tree. Aucun de ces écarts n’est présenté comme une parité complète.

## Validation

Les preuves ciblées, suites complètes, captures et version du paquet sont référencées dans le TODO. Les familles de migration et l’audit natif étendu restent ouverts.
