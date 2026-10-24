CFLAGS ?= -O2 -Wall -Wextra
s3bopus: s3bopus.c
	$(CC) $(CFLAGS) -o $@ $< $$(pkg-config --cflags --libs x11 xft) -lpthread

# Essai sans écran : démarre sous Xvfb, copie un dossier par la file avec xdotool, vérifie le résultat
test: s3bopus
	bash test.sh

clean:
	rm -f s3bopus
