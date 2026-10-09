Font aggiuntivi (facoltativi)
=============================

L'immagine Docker contiene solo font open source. Arial, Calibri, Times New Roman ecc. sono
sostituiti da font con le stesse metriche (Liberation Sans, Carlito...).

Se l'azienda ha la licenza dei font originali, copia qui i file .ttf/.otf: vengono montati in
sola lettura nei container e preferiti ai sostituti, senza ricostruire l'immagine.
Dopo averli aggiunti: docker compose restart

Conta soprattutto per PDF, XPS, immagini e video, dove il watermark è disegnato con il font del
server. Nei file Office il font è indicato per nome e lo applica il PC che apre il documento.
