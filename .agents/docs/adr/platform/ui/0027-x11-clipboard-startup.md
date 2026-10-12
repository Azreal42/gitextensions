# Presse-papiers au premier démarrage X11

Statut : accepté.

Intention : les gestes Copier du viewer et des champs texte fonctionnent même lorsque Git Extensions est le premier client du serveur X11, sans dépendre d’un gestionnaire de presse-papiers.

Avalonia 11.3.22 ne crée pas les atoms absents lors de leur mise en cache. `LinuxClipboardProtocol` initialise uniquement les sept noms du protocole, avant le démarrage GUI/askpass, et conserve sa connexion pendant le lifetime. Aucun owner ni contenu n’est fabriqué ; les modes CLI restent indépendants du display.

Repères : `src/linux/GitExtensions.Desktop/{Program,LinuxClipboardProtocol}.cs`. Reproduction rouge et preuve native de la classe de production sous `artifacts/linux/test-results/x11-clipboard-independent/` : TextBox et AvaloniaEdit, copie API et Ctrl+C, serveur neuf sans seed externe ni manager. Le paquet est validé séparément depuis le commit exact.
