# Media3 ships its own rules; nothing in Dingus uses reflection.
-dontwarn org.checkerframework.**
-dontwarn com.google.errorprone.annotations.**

# WireGuard (native engine calls back into these)
-keep class com.wireguard.** { *; }
