# Presse-papiers au premier démarrage X11

Statut : accepté.

Intention : les gestes Copier du viewer et des champs texte fonctionnent même lorsque Git Extensions est le premier client du serveur X11, sans dépendre d’un gestionnaire de presse-papiers.

Avalonia 11.3.22 ne crée pas les atoms absents lors de leur mise en cache. `LinuxClipboardProtocol` initialise uniquement les sept noms du protocole, avant le démarrage GUI/askpass, et conserve sa connexion pendant le lifetime. Aucun owner ni contenu n’est fabriqué ; les modes CLI restent indépendants du display.

Repères : `src/linux/GitExtensions.Desktop/{Program,LinuxClipboardProtocol}.cs`. Reproduction et copies natives sur serveur neuf sans seed/manager : `artifacts/linux/test-results/x11-clipboard-independent/` et snapshot `ca86aafd5`. Compteurs/captures restent dans ces preuves.
