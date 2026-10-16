# Turns pixel art (apple.txt: "colour <char> <rrggbb>" lines, then one row of characters per pixel row) into
# coloured half blocks for a terminal: two pixel rows per line, "▀" in the upper pixel's colour on the lower pixel's.
# "." pixels are left as the terminal's own background. Each line is indented two spaces and padded to full width.
# With -v ppm=1 it writes the picture as a PPM image instead, for previews.
function hex(s,    i, n) {
	n = 0
	for (i = 1; i <= length(s); i++) n = n * 16 + index("0123456789abcdef", tolower(substr(s, i, 1))) - 1
	return n
}
/^#/ || /^$/ { next }
$1 == "colour" { rgb[$2] = hex(substr($3, 1, 2)) ";" hex(substr($3, 3, 2)) ";" hex(substr($3, 5, 2)); next }
{ rows[h++] = $0; if (length($0) > w) w = length($0) }
function pixel(x, y) { return y < h ? substr(rows[y], x + 1, 1) : "." }
END {
	if (ppm) {
		printf "P3\n%d %d\n255\n", w, h
		for (y = 0; y < h; y++)
			for (x = 0; x < w; x++) {
				c = pixel(x, y)
				printf "%s\n", c in rgb ? rgb[c] : "0;0;0"
			}
		exit
	}
	for (y = 0; y < h; y += 2) {
		line = "  "
		for (x = 0; x < w; x++) {
			top = pixel(x, y); bottom = pixel(x, y + 1)
			if (top != "." && bottom != ".")
				line = line "\033[38;2;" rgb[top] ";48;2;" rgb[bottom] "m\342\226\200\033[0m"
			else if (top != ".")
				line = line "\033[38;2;" rgb[top] "m\342\226\200\033[0m"
			else if (bottom != ".")
				line = line "\033[38;2;" rgb[bottom] "m\342\226\204\033[0m"
			else
				line = line " "
		}
		print line
	}
}
