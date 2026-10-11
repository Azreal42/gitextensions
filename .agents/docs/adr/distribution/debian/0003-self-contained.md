# ADR 0003 · Distribution Debian

Statut : accepté, validation en cours.

Besoin : installer un `.deb` et lancer depuis le menu ou un terminal, sans installer le SDK .NET.

Décision : publier le runtime .NET avec l'application, installer les fichiers sous `/usr/lib/gitextensions`, les ressources bureau sous `/usr/share`, et garder les préférences utilisateur dans XDG. Git et les bibliothèques système restent des dépendances du paquet.

Conséquence : vérifier l'extraction, les dépendances, le lancement graphique et l'installation du paquet ; pas de post-install qui modifie des dépôts ou des identifiants.

Repères : `eng/linux/`, `setup/linux/`, [déploiement Avalonia Linux](https://docs.avaloniaui.net/docs/deployment/linux).
