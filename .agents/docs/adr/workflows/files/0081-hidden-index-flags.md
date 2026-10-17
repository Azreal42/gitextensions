# ADR 0081 — Retrouver un fichier masqué par un flag d’index

## Intention

Un fichier marqué Skip worktree ou Assume unchanged peut disparaître du diff. L’utilisateur doit pouvoir le retrouver et retirer le bit dans l’interface, sans commande Git manuelle ni staging implicite.

## Décision

- La route est **Commit → Unstaged → Settings → Show skip-worktree files / Show assumed-unchanged files**, puis le menu de la ligne révélée. Ces choix d’affichage restent dans la session du contrôle.
- L’index fournit les bits réels et les chemins suivis. Révéler un chemin ne fabrique ni modification de contenu ni entrée staged ; garder son vrai statut s’il existe déjà et une seule ligne s’il porte les deux bits. Stage all conserve les protections des chemins marqués.
- Le menu retire ou pose seulement le bit choisi sur les chemins littéraux admissibles. Relire après l’opération, y compris après une issue partielle, et conserver le diagnostic Git initial. La première flèche après retrait poursuit la navigation depuis la ligne choisie. Restituer seulement l’origine réelle, encore valide, de cette action ; une fermeture ou une ouverture clavier ne doit pas rejouer un ancien focus.
- Ignore/Exclude se réactivent quand la première lecture se termine, dans le même popup. Leur activation suit la disponibilité de Commit et la sélection courante.
- File tree représente une révision historique, sans bits live inventés sur ses blobs. Le ShowSkip de FormDiff Windows à deux révisions ne constitue pas une route de retour fonctionnelle : sa propagation et son refresh ne sont pas raccordés. Aucun équivalent actif n’est revendiqué pour ce contrôle sans effet ; la reprise après fermeture de Compare se fait dans Commit.

## Repères et validation

`IndexFlagFileList`, `CommitDialog.IndexFlags`, `CommitDialog.RefreshDataAsync/StageablePathsAsync`, `GitRepository.IndexFlags` et `CommitIndexFlagTests` portent le contrat ; le code reste la source de vérité.

Tranche `a0ba1c140` : ciblés, full et paquet exact, puis cycles natifs indépendants et premier geste revus selon le [TODO](../../../todo.md). Le full d15 1334/1334 et ses deux premières Down rouges restent conservés. Les deux cycles ne certifient ni le cas natif combinant les bits ni Stage all natif. FormCommit/Browse/FormDiff restent N2.
