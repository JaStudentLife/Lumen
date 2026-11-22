namespace Lumen
{
    /// <summary>The search window's visual tree, parsed at runtime with XamlReader.</summary>
    internal static class WindowXaml
    {
        public const string Markup = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='Lumen' Width='760' SizeToContent='Height' WindowStyle='None' ResizeMode='NoResize'
        ShowInTaskbar='False' Topmost='True' Background='Transparent' WindowStartupLocation='Manual'
        FontFamily='Segoe UI Variable Text, Segoe UI' UseLayoutRounding='True' SnapsToDevicePixels='True'>
  <Window.Resources>
    <SolidColorBrush x:Key='Fg' Color='#F2FFFFFF'/>
    <SolidColorBrush x:Key='Fg2' Color='#A6FFFFFF'/>
    <SolidColorBrush x:Key='Fg3' Color='#80FFFFFF'/>
    <SolidColorBrush x:Key='Line' Color='#1FFFFFFF'/>
    <SolidColorBrush x:Key='Hover' Color='#14FFFFFF'/>
    <SolidColorBrush x:Key='Accent' Color='#E60A84FF'/>
    <SolidColorBrush x:Key='OnAccent' Color='#FFFFFFFF'/>
    <SolidColorBrush x:Key='OnAccent2' Color='#D9FFFFFF'/>
    <SolidColorBrush x:Key='PreviewBg' Color='#0AFFFFFF'/>
    <SolidColorBrush x:Key='Chip' Color='#1FFFFFFF'/>
    <SolidColorBrush x:Key='Surface' Color='#00000000'/>

    <DataTemplate x:Key='RowTemplate'>
      <Grid>
        <TextBlock Text='{Binding Title}' Visibility='{Binding HeaderVisibility}' FontSize='11.5' FontWeight='SemiBold'
                   Foreground='{DynamicResource Fg3}' Margin='12,10,0,3'/>
        <Grid Visibility='{Binding ItemVisibility}' Height='46'>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='46'/>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
          </Grid.ColumnDefinitions>
          <Border Width='30' Height='30' CornerRadius='7' Background='{Binding GlyphBrush}' Visibility='{Binding GlyphVisibility}'
                  HorizontalAlignment='Center' VerticalAlignment='Center'>
            <TextBlock Text='{Binding Glyph}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='15' Foreground='White'
                       HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <Image Source='{Binding Icon}' Width='32' Height='32' Visibility='{Binding IconVisibility}'
                 RenderOptions.BitmapScalingMode='HighQuality' HorizontalAlignment='Center' VerticalAlignment='Center'/>
          <StackPanel Grid.Column='1' VerticalAlignment='Center' Margin='4,0,8,0'>
            <TextBlock x:Name='T' Text='{Binding Title}' FontSize='14.5' Foreground='{DynamicResource Fg}' TextTrimming='CharacterEllipsis'/>
            <TextBlock x:Name='S' Text='{Binding SubtitleText}' FontSize='12' Foreground='{DynamicResource Fg2}' TextTrimming='CharacterEllipsis'
                       Visibility='{Binding SubtitleVisibility}' Margin='0,1,0,0'/>
          </StackPanel>
          <TextBlock x:Name='H' Grid.Column='2' Text='{Binding Hint}' Visibility='{Binding HintVisibility}' FontSize='12'
                     Foreground='{DynamicResource Fg3}' VerticalAlignment='Center' Margin='0,0,10,0'/>
        </Grid>
      </Grid>
      <DataTemplate.Triggers>
        <DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'>
          <Setter TargetName='T' Property='Foreground' Value='{DynamicResource OnAccent}'/>
          <Setter TargetName='S' Property='Foreground' Value='{DynamicResource OnAccent2}'/>
          <Setter TargetName='H' Property='Foreground' Value='{DynamicResource OnAccent2}'/>
        </DataTrigger>
        <DataTrigger Binding='{Binding Armed}' Value='True'>
          <Setter TargetName='S' Property='FontWeight' Value='SemiBold'/>
        </DataTrigger>
      </DataTemplate.Triggers>
    </DataTemplate>

    <Style x:Key='RowStyle' TargetType='ListBoxItem'>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ListBoxItem'>
            <Border x:Name='Bd' CornerRadius='8' Background='Transparent' Margin='8,0,8,0' Padding='2,0,4,0'>
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='{DynamicResource Hover}'/>
              </Trigger>
              <Trigger Property='IsSelected' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='{DynamicResource Accent}'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
      <Style.Triggers>
        <DataTrigger Binding='{Binding IsHeader}' Value='True'>
          <Setter Property='IsHitTestVisible' Value='False'/>
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </Window.Resources>

  <Grid x:Name='Root' Background='{DynamicResource Surface}'>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='Auto'/>
    </Grid.RowDefinitions>

    <!-- Search bar -->
    <Grid Grid.Row='0' Height='62' Margin='20,0,16,0'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/>
      </Grid.ColumnDefinitions>
      <TextBlock Text='&#xE721;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='20' Foreground='{DynamicResource Fg2}'
                 VerticalAlignment='Center' Margin='0,1,0,0'/>
      <TextBlock x:Name='Placeholder' Grid.Column='1' Text='Lumen Search' FontSize='24' FontWeight='Light'
                 Foreground='{DynamicResource Fg3}' VerticalAlignment='Center' Margin='14,0,0,1' IsHitTestVisible='False'/>
      <TextBox x:Name='Query' Grid.Column='1' FontSize='24' FontWeight='Light' BorderThickness='0' Background='Transparent'
               Foreground='{DynamicResource Fg}' CaretBrush='{DynamicResource Fg}' SelectionBrush='{DynamicResource Accent}'
               VerticalAlignment='Center' Margin='12,0,0,0' Padding='0' FocusVisualStyle='{x:Null}'/>
      <Border x:Name='Chip' Grid.Column='2' CornerRadius='6' Background='{DynamicResource Chip}' Padding='8,3,10,3'
              VerticalAlignment='Center' Visibility='Collapsed' Margin='8,0,0,0'>
        <StackPanel Orientation='Horizontal'>
          <Image x:Name='ChipIcon' Width='16' Height='16' Margin='0,0,6,0' RenderOptions.BitmapScalingMode='HighQuality'/>
          <TextBlock x:Name='ChipText' FontSize='12.5' Foreground='{DynamicResource Fg}' VerticalAlignment='Center'/>
        </StackPanel>
      </Border>
      <TextBlock x:Name='EmptyHint' Grid.Column='2' Text='? for commands' FontSize='12' Foreground='{DynamicResource Fg3}'
                 VerticalAlignment='Center' Margin='8,0,4,0'/>
    </Grid>

    <Border x:Name='Divider' Grid.Row='1' Height='1' Background='{DynamicResource Line}' Visibility='Collapsed'/>

    <!-- Results + preview -->
    <Grid x:Name='Body' Grid.Row='2' Height='420' Visibility='Collapsed'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='*'/>
        <ColumnDefinition x:Name='PreviewColumn' Width='290'/>
      </Grid.ColumnDefinitions>
      <ListBox x:Name='List' Background='Transparent' BorderThickness='0' Padding='0,4,0,6' Focusable='False'
               ItemTemplate='{StaticResource RowTemplate}' ItemContainerStyle='{StaticResource RowStyle}'
               ScrollViewer.HorizontalScrollBarVisibility='Disabled' ScrollViewer.VerticalScrollBarVisibility='Hidden'
               VirtualizingStackPanel.IsVirtualizing='True' VirtualizingStackPanel.VirtualizationMode='Recycling'/>
      <Border x:Name='Preview' Grid.Column='1' Background='{DynamicResource PreviewBg}' BorderBrush='{DynamicResource Line}' BorderThickness='1,0,0,0'>
        <ScrollViewer VerticalScrollBarVisibility='Hidden' Focusable='False'>
          <StackPanel Margin='20,22,20,16'>
            <Grid Height='110' Margin='0,0,0,14'>
              <Image x:Name='PvImage' MaxWidth='240' MaxHeight='110' HorizontalAlignment='Center' VerticalAlignment='Center'
                     RenderOptions.BitmapScalingMode='HighQuality'/>
              <Border x:Name='PvGlyphTile' Width='72' Height='72' CornerRadius='16' HorizontalAlignment='Center' VerticalAlignment='Center'>
                <TextBlock x:Name='PvGlyph' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='34' Foreground='White'
                           HorizontalAlignment='Center' VerticalAlignment='Center'/>
              </Border>
            </Grid>
            <TextBlock x:Name='PvTitle' FontSize='16' FontWeight='SemiBold' Foreground='{DynamicResource Fg}' TextWrapping='Wrap'
                       TextAlignment='Center' Margin='0,0,0,3'/>
            <TextBlock x:Name='PvKind' FontSize='12' Foreground='{DynamicResource Fg2}' TextAlignment='Center' TextWrapping='Wrap'/>
            <Border Height='1' Background='{DynamicResource Line}' Margin='0,14,0,10'/>
            <ItemsControl x:Name='PvDetails' Focusable='False'>
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <Grid Margin='0,3'>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width='72'/>
                      <ColumnDefinition Width='*'/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Text='{Binding Key}' FontSize='11.5' Foreground='{DynamicResource Fg3}'/>
                    <TextBlock Grid.Column='1' Text='{Binding Value}' FontSize='11.5' Foreground='{DynamicResource Fg2}' TextWrapping='Wrap'/>
                  </Grid>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock x:Name='PvNote' FontSize='12' Foreground='{DynamicResource Fg2}' TextWrapping='Wrap' Margin='0,8,0,0' LineHeight='18'/>
          </StackPanel>
        </ScrollViewer>
      </Border>
    </Grid>

    <!-- Footer: what the keys do for the selected row -->
    <Border x:Name='Footer' Grid.Row='3' Height='32' BorderBrush='{DynamicResource Line}' BorderThickness='0,1,0,0' Visibility='Collapsed'>
      <Grid Margin='18,0,16,0'>
        <TextBlock x:Name='FooterLeft' FontSize='11.5' Foreground='{DynamicResource Fg3}' VerticalAlignment='Center'/>
        <TextBlock x:Name='FooterRight' FontSize='11.5' Foreground='{DynamicResource Fg3}' VerticalAlignment='Center' HorizontalAlignment='Right'/>
      </Grid>
    </Border>
  </Grid>
</Window>";
    }
}
