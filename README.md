xamarin-android-schema-generator is an Android layout.xml schema generator.

WARNING: Android layout xml does not fit well with XML schema restrictions.
XML Schema is not extensible enough to accept non-standard components nor
non-standard attributes which are (still) in android XML namespace (due to
the limitation of element/attribute wildcards). So, you CANNOT use the
resulting schema to VALIDATE your layout xml resources.
It should be used only for auto-complete element/attribute candidates.

To generate schemas, run the tool with path to Android SDK:

	mono xamarin-android-schema-generator.exe /path/to/android-sdk-(os)
