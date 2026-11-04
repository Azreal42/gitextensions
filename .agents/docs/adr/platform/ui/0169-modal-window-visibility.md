# ADR 0169 · Visibilité des fenêtres modales

Statut : garde commune intégrée depuis le correctif préexistant du checkout principal ; onze contrats headless passent sur Linux. Audit natif étendu ouvert.

Les trois points d'entrée Avalonia initialisent ModalWindowActivation. La garde observe l'ouverture des dialogs puis attend Opened pour disposer de leur Owner réel. Elle retire CanMinimize pendant la session modale et restaure une modale minimisée seulement si tous ses propriétaires restent visibles et non minimisés. La minimisation de toute l'application reste autorisée. Une restauration active la plus profonde modale visible, sans voler le focus à son enfant.

Hide/Closed retirent les abonnements et rendent CanMinimize à sa valeur précédente. Réouvrir la fenêtre reconstruit la chaîne réelle de propriétaires ; un Close annulé conserve la protection. Aucun polling ajouté.

Les quatre fichiers produit sont copiés byte pour byte depuis les changements déjà présents dans le checkout principal ; ses quatorze fichiers préexistants restent inchangés. Le test copié reçoit seulement la catégorie CrossPlatform. Onze cas vérifient normal/maximisé, minimisation de l'application, restauration différée, fermeture, modeless, modales imbriquées et réouvertures. Ils passent avec les autres contrats du lot ADR0168 : 236 Desktop ciblés, 14 POSIX, deux régressions de statut, 1 210 Core, 42 Plugins et cinq contrôles Debian, sans ignoré. Cela ne qualifie ni les window managers natifs Windows/Mac ni toute la suite Desktop de ce nouveau lot.

Repères : ModalWindowActivation, App, AskpassApplication, NativeRebaseEditorApplication, ModalWindowActivationTests. Preuves : artifacts/cross-platform/20261104/shell-discovery/validation-review.json, preexisting-modal-review.json et modal-integrated-source.json.
