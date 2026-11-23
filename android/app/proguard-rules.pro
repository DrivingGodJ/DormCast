# Keep JS bridge methods exposed to DormCast web receiver
-keepclassmembers class com.dormcast.receiver.MainActivity$DormCastBridge {
    @android.webkit.JavascriptInterface <methods>;
}
