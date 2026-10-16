# 0069 · Ajout de sous-module

Statut : périmètre ciblé testé ; parité native exhaustive encore ouverte.

Retrouver FormAddSubmodule : trois champs Source/Local path/Branch, Browse de la source, Force et Add, sans action par défaut. Conserver les champs vides à l’ouverture, l’historique partagé de Clone en lecture et la proposition du nom local à chaque nouvelle source. Les URL, chemins et branches restent éditables ; ne pas ajouter automatiquement le dépôt aux récents.

Partager le contrôle Source et SuggestAppend avec les URL de Remotes : liste filtrée et suffixe sélectionné après une frappe réelle, navigation clavier et clic, sans compléter un chargement programmatique. Le nom proposé reprend le calcul Clone. Les chemins Linux restent sensibles à la casse et les antislashs sont littéraux.

Git exécute `submodule add`, avec `-b` et `-f` explicites. Il possède le clone, la configuration et le staging de `.gitmodules`/gitlink. Une destination déjà Git valide est admise ; Force ne devient pas une autorisation de supprimer des fichiers. Revalider les identités racine/destination/métadonnées, refuser les sorties du dépôt et traversées d’alias ; les gardes ne promettent pas une transaction contre toutes les écritures externes concurrentes.

Les branches suggérées doivent provenir de la source qu’utilisera l’ajout : combiner les URL relatives avec la dernière URL brute du remote de suivi, puis laisser Git appliquer insteadOf. Le choix du seul remote sans origin dépend de Git depuis 2.51. Un remote défini uniquement par `-c` pour la requête empêche un nom de dossier d’être réinterprété comme un autre remote. Une requête obsolète est annulée et drainée ; une branche saisie pendant l’attente reste intacte.

Une erreur ou une annulation peut survenir après création du dossier ou modification de l’index/configuration. Conserver l’erreur native et ces effets ; ne pas effacer pour simuler un rollback. Contrairement au Close inconditionnel Windows après FormProcess, conserver les champs après échec/Abort. Fermer après succès acquitté ; un Process fermé sans OK conserve la fenêtre et indique l’état réel. La fermeture du propriétaire attend le drainage des transports. Browse relit son état après une tentative potentiellement mutante, une fois le modal libéré.

Repères : AddSubmoduleDialog/Branches, RepositorySourceControls, MainWindow.Workflows, GitRepository.SubmoduleAdd.*, GitSubmoduleAddOptions. Référence Windows : SubmodulesDialog/FormAddSubmodule et Commands.Arguments.

Validation : AddSubmoduleDialogTests, SubmoduleAddScenarios et régressions Remotes/Clone, liés à leur source dans le TODO. Git 2.53 exécuté ; seuil 2.51 fondé sur les sources versionnées, sans exécution d’un ancien Git. FormSubmodules et le conflit de gitlink restent des portages distincts ; aucun N4 ni clôture de la famille sous-modules.
