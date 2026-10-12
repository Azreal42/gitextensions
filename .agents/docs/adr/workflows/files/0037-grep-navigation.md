# ADR 0037 · Recherche dans les fichiers du commit

Statut : sous-périmètre livré et vérifié ; état du paquet et limites dans le [TODO](../../../todo.md).

Besoin : retrouver le petit formulaire FindInCommitFilesGitGrep et son filtre dans la liste de fichiers, puis naviguer vers la ligne du résultat dans le lecteur existant.

Le résultat conserve le hash recherché, les octets du chemin et de la ligne, le codec et la colonne Git. Décoder le contenu avec le codec de fichier, distinct des métadonnées UTF-8 ; convertir la colonne en position du texte seulement quand la lecture est sans perte. Un chemin non UTF-8 ne doit pas être ouvert sous un nom de remplacement. La liste native des fichiers conserve aussi les correspondances binaires ; ouvrir leur blob dans le reader sans inventer de coordonnées.

La recherche suit le commit et le dépôt actifs ; leurs changements annulent les anciennes lectures. Fermer Find conserve un filtre actif, le masquer ou effacer l'expression rétablit la liste. MRU de session, casse/mots et options Git gardent le comportement original. Les options qui remplacent les lignes par des comptes/fragments et le contexte binaire ambigu sont refusés explicitement plutôt que reconstruits pour la navigation.

Repères : FormFindInCommitFilesGitGrep, GitModule.GetGrepFile ; MainWindow.Grep, FindInCommitFilesDialog, GitRepository.Grep, GitGrepModels, GitFileEncodingCodec ; GrepScenarios et GrepDialogTests. Les chemins Linux à octets invalides restent une limite générale ouverte.
