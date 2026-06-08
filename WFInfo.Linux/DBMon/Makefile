TARGET = WFInfo.DbMon.exe

$(TARGET): dbmon.c
	zig cc -target x86_64-windows-gnu -O2 -o $@ $<

clean:
	rm -f $(TARGET)

.PHONY: clean