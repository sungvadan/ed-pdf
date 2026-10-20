# Folio — feuille de route

Éditeur PDF natif macOS (clone de PDF Expert), construit par étapes.

## ✅ Étape 1 — Lecteur
- Ouvrir / enregistrer / nouveau document (DocumentGroup), onglets et fichiers récents natifs
- Vignettes des pages dans la barre latérale
- Zoom (+, -, taille réelle, ajuster), modes page unique / continu / deux pages
- Navigation entre pages, indicateur « page x / n »
- Recherche plein texte avec surlignage et résultat suivant/précédent (⌘G / ⇧⌘G)

## ⬜ Étape 2 — Annotations
- Surligner / souligner / barrer le texte sélectionné (couleurs)
- Notes (post-it), zones de texte libres
- Dessin à main levée (encre), formes (rectangle, ellipse, ligne, flèche)
- Sélection / déplacement / suppression d'annotations, panneau de propriétés
- Annuler / rétablir (UndoManager → marque le document comme modifié)

## ⬜ Étape 3 — Gestion des pages
- Pivoter, supprimer, dupliquer, réordonner (glisser-déposer dans les vignettes)
- Insérer une page vierge ou depuis un autre PDF / une image
- Fusionner des PDF, extraire des pages

## ⬜ Étape 4 — Formulaires & signature
- Remplissage des champs de formulaire
- Signature dessinée/enregistrée, tampon, image

## ⬜ Étape 5 — Édition de texte & extras
- Ajouter du texte, masquer/caviarder (redaction)
- Édition du texte existant (difficile avec PDFKit : surcouche « effacer + réécrire »)
- Signets / table des matières, export image, compression, mot de passe
- Icône de l'app, préférences
