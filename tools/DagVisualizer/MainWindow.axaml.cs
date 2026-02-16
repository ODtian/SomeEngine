using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Pipeline;

namespace SomeEngine.DagVisualizer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoadMeshClick(object sender, RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open Mesh File",
            FileTypeFilter = new[] { new FilePickerFileType("Mesh File") { Patterns = new[] { "*.mesh" } } }
        };

        var result = await StorageProvider.OpenFilePickerAsync(options);
        if (result.Count > 0)
        {
            LoadMesh(result[0].Path.LocalPath);
        }
    }

    private void LoadMesh(string path)
    {
        try
        {
            var meshAsset = MeshAssetSerializer.Load(path);
            if (meshAsset.Payload == null) return;

            var clusters = new List<GPUCluster>();
            var span = meshAsset.Payload.Value.Span;
            int offset = 0;

            while (offset < span.Length)
            {
                var header = MemoryMarshal.Read<MeshPageHeader>(span.Slice(offset, MeshPageHeader.Size));
                int clusterOffset = (int)header.ClustersOffset;
                int clusterSize = Marshal.SizeOf<GPUCluster>();
                
                for (int i = 0; i < header.ClusterCount; i++)
                {
                    var cluster = MemoryMarshal.Read<GPUCluster>(span.Slice(offset + clusterOffset + i * clusterSize, clusterSize));
                    clusters.Add(cluster);
                }
                offset += (int)header.PageSize;
            }

            DagView.SetClusters(clusters);
            MeshInfo.Text = $"Loaded {path}: {clusters.Count} clusters";
        }
        catch (Exception ex)
        {
            MeshInfo.Text = $"Error: {ex.Message}";
        }
    }
}
